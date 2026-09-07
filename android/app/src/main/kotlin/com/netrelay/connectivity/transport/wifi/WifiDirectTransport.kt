package com.netrelay.connectivity.transport.wifi

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.location.LocationManager
import android.net.wifi.WifiManager
import android.net.wifi.p2p.WifiP2pConfig
import android.net.wifi.p2p.WifiP2pGroup
import android.net.wifi.p2p.WifiP2pInfo
import android.net.wifi.p2p.WifiP2pManager
import android.os.Build
import com.netrelay.connectivity.transport.ITransport
import com.netrelay.connectivity.util.Logger
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.Inet4Address
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.net.ServerSocket
import java.net.Socket
import java.nio.charset.StandardCharsets
import kotlin.coroutines.resume

/**
 * Wi-Fi Direct/hotspot transport backed by a TCP listener.
 */
class WifiDirectTransport(
    private val context: Context,
    private val manager: WifiP2pManager?,
    private val channel: WifiP2pManager.Channel?
) : ITransport {

    private val logger = Logger
    private val tag = "WifiDirectTransport"
    private val transportScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val disconnectMutex = Mutex()
    private val preferences = context.getSharedPreferences(PREFERENCES_NAME, Context.MODE_PRIVATE)

    private val _incomingData = MutableSharedFlow<ByteArray>(extraBufferCapacity = 32)
    private val _stateFlow = MutableStateFlow(ITransport.TransportState.DISCONNECTED)
    private val _errorFlow = MutableSharedFlow<ITransport.TransportError>(extraBufferCapacity = 8)

    @Volatile
    private var configuredNetworkName = normalizeNetworkName(
        preferences.getString(KEY_NETWORK_NAME, null).orEmpty()
    ) ?: buildDefaultNetworkName()

    @Volatile
    private var configuredPassphrase = preferences.getString(KEY_PASSPHRASE, DEFAULT_PASSPHRASE)
        ?.takeIf { isValidPassphrase(it) }
        ?: DEFAULT_PASSPHRASE

    @Volatile
    private var activeNetworkName = ""

    @Volatile
    private var activePassphrase = ""

    @Volatile
    private var recreateGroupOnNextConnect = false

    @Volatile
    private var serverSocket: ServerSocket? = null

    @Volatile
    private var clientSocket: Socket? = null

    @Volatile
    private var activeLocalAddress: String? = null

    @Volatile
    private var activeP2pAddress: String? = null

    @Volatile
    private var activeP2pInterfaceName: String? = null

    @Volatile
    private var currentState = ITransport.TransportState.DISCONNECTED

    @Volatile
    private var listeningPort = DEFAULT_PORT

    init {
        logger.info(tag, "Wi-Fi Direct/hotspot transport initialized")
    }

    override suspend fun connect(target: String): Boolean {
        if (serverSocket != null) {
            logger.debug(tag, "Wi-Fi listener already active on ${getServerEndpoint()}")
            return true
        }

        val port = parsePort(target)
        return try {
            logger.info(tag, "[NETRELAY-WFD] Start requested")
            activeP2pAddress = null
            activeP2pInterfaceName = null
            activeLocalAddress = null
            activeNetworkName = ""
            activePassphrase = ""
            listeningPort = port
            updateState(ITransport.TransportState.CONNECTING)

            prepareWifiDirectGroup()

            val p2pAddress = activeP2pAddress
                ?: throw IllegalStateException("Wi-Fi Direct group has no usable local P2P IPv4 address")
            serverSocket = ServerSocket().apply {
                reuseAddress = true
                bind(InetSocketAddress(p2pAddress, port))
            }
            activeLocalAddress = p2pAddress

            logger.info(tag, "[NETRELAY-WFD] Control listening on $p2pAddress:$port")
            logger.info(tag, "Wi-Fi listener started on ${getServerEndpoint()}")
            transportScope.launch { acceptLoop() }
            true
        } catch (e: CancellationException) {
            logger.info(tag, "[NETRELAY-WFD] P2P address wait cancelled")
            throw e
        } catch (e: Exception) {
            logger.error(tag, "Failed to start Wi-Fi listener", e)
            emitError(ITransport.TransportError(1001, "Failed to start Wi-Fi listener", e))
            updateState(ITransport.TransportState.ERROR)
            false
        }
    }

    override suspend fun disconnect() = disconnectMutex.withLock {
        try {
            logger.info(tag, "Stopping Wi-Fi listener")
            updateState(ITransport.TransportState.DISCONNECTING)
            closeControlListener()
            clearActiveGroupState()
            updateState(ITransport.TransportState.DISCONNECTED)
        } catch (e: Exception) {
            logger.error(tag, "Wi-Fi disconnect error", e)
            emitError(ITransport.TransportError(1002, "Wi-Fi disconnect error", e))
            updateState(ITransport.TransportState.ERROR)
        }
    }

    /**
     * Explicit user disconnect. Unlike the normal transport/lifecycle cleanup, this
     * also removes the autonomous Wi-Fi Direct group and verifies the callback result.
     */
    suspend fun disconnectAndRemoveGroup(): Boolean = disconnectMutex.withLock {
        logger.info(tag, "[NETRELAY-WFD] Disconnect requested")
        updateState(ITransport.TransportState.DISCONNECTING)

        return@withLock try {
            closeControlListener()
            stopPeerDiscoverySafely()

            val before = withTimeoutOrNull(GROUP_INFO_CALLBACK_TIMEOUT_MS) {
                requestConnectionInfoSafely()
            }
            if (before != null && !before.groupFormed) {
                logger.info(tag, "[NETRELAY-WFD] groupFormed=false")
                finishExplicitDisconnect()
                return@withLock true
            }

            logger.info(tag, "[NETRELAY-WFD] Removing P2P group")
            val removeSucceeded = removeWifiDirectGroupForDisconnectSafely()
            val verified = verifyGroupRemoved()
            if (!removeSucceeded && !verified) {
                logger.warn(tag, "[NETRELAY-WFD] Wi-Fi Direct group removal was not verified")
                updateState(ITransport.TransportState.ERROR)
                return@withLock false
            }

            if (!verified) {
                logger.warn(tag, "[NETRELAY-WFD] removeGroup callback succeeded but groupFormed=false was not observed")
                updateState(ITransport.TransportState.ERROR)
                return@withLock false
            }

            logger.info(tag, "[NETRELAY-WFD] groupFormed=false")
            finishExplicitDisconnect()
            true
        } catch (e: CancellationException) {
            logger.info(tag, "[NETRELAY-WFD] Disconnect cancelled")
            throw e
        } catch (e: Exception) {
            logger.error(tag, "Wi-Fi Direct explicit disconnect error", e)
            emitError(ITransport.TransportError(1002, "Wi-Fi Direct explicit disconnect error", e))
            updateState(ITransport.TransportState.ERROR)
            false
        }
    }

    override suspend fun send(data: ByteArray): Boolean {
        val socket = clientSocket
        return try {
            if (socket == null || socket.isClosed || !socket.isConnected) {
                logger.warn(tag, "Send requested with no active Wi-Fi client")
                return false
            }

            socket.getOutputStream().apply {
                write(data)
                write('\n'.code)
                flush()
            }

            logger.debug(tag, "Sent ${data.size} bytes via Wi-Fi")
            true
        } catch (e: Exception) {
            logger.error(tag, "Wi-Fi send error", e)
            emitError(ITransport.TransportError(1003, "Wi-Fi send error", e))
            updateState(ITransport.TransportState.ERROR)
            false
        }
    }

    override fun observeIncoming(): Flow<ByteArray> = _incomingData.asSharedFlow()

    override fun observeState(): Flow<ITransport.TransportState> = _stateFlow.asStateFlow()

    override fun observeErrors(): Flow<ITransport.TransportError> = _errorFlow.asSharedFlow()

    override fun getState(): ITransport.TransportState = currentState

    override fun isConnected(): Boolean {
        val socket = clientSocket
        return currentState == ITransport.TransportState.CONNECTED &&
            socket != null &&
            socket.isConnected &&
            !socket.isClosed
    }

    override fun getTransportType(): String = "Wi-Fi Direct / Hotspot"

    override suspend fun cleanup() {
        logger.info(tag, "Cleaning up Wi-Fi transport")
        disconnect()
        transportScope.cancel()
    }

    fun getServerEndpoint(): String {
        val resolvedAddress = activeLocalAddress?.takeIf { isUsableListenerAddress(it) }
            ?: activeP2pAddress?.takeIf { isUsableListenerAddress(it) }
            ?: resolveIpv4Address()
        return "$resolvedAddress:$listeningPort"
    }

    fun getConnectedClientEndpoint(): String {
        val socket = clientSocket ?: return "No client connected"
        val host = socket.inetAddress?.hostAddress ?: "Unknown host"
        return "$host:${socket.port}"
    }

    fun getWifiDirectNetworkName(): String = activeNetworkName

    fun getWifiDirectPassphrase(): String = activePassphrase

    /** Runtime proof used by the UI/service; configured preferences alone are not an active group. */
    fun hasActiveGroup(): Boolean {
        return activeP2pAddress?.let { isUsableListenerAddress(it) } == true &&
            activeNetworkName.isNotBlank() &&
            serverSocket?.isClosed == false
    }

    fun isListening(): Boolean = serverSocket?.isClosed == false

    fun updateWifiDirectSettings(networkName: String?, passphrase: String): Boolean {
        val normalizedPassphrase = passphrase.trim()
        if (!isValidPassphrase(normalizedPassphrase)) {
            logger.warn(tag, "Rejected Wi-Fi Direct passphrase; it must be ASCII and 8-63 chars")
            return false
        }

        val normalizedNetworkName = normalizeNetworkName(networkName.orEmpty()) ?: configuredNetworkName
        val changed = normalizedNetworkName != configuredNetworkName ||
            normalizedPassphrase != configuredPassphrase

        configuredNetworkName = normalizedNetworkName
        configuredPassphrase = normalizedPassphrase
        preferences.edit()
            .putString(KEY_NETWORK_NAME, configuredNetworkName)
            .putString(KEY_PASSPHRASE, configuredPassphrase)
            .apply()

        if (changed) {
            recreateGroupOnNextConnect = true
        }

        logger.info(
            tag,
            "Wi-Fi Direct settings saved: name=$configuredNetworkName passphrase=${maskPassphrase(configuredPassphrase)}"
        )
        return true
    }

    private suspend fun acceptLoop() {
        while (serverSocket != null) {
            try {
                val acceptedSocket = serverSocket?.accept() ?: break
                closeClientSocket()
                clientSocket = acceptedSocket
                activeLocalAddress = acceptedSocket.localAddress?.hostAddress
                updateState(ITransport.TransportState.CONNECTED)
                logger.info(tag, "Wi-Fi client connected: ${getConnectedClientEndpoint()}")
                readLoop(acceptedSocket)
            } catch (e: Exception) {
                if (serverSocket == null) {
                    break
                }

                logger.error(tag, "Wi-Fi accept loop error", e)
                emitError(ITransport.TransportError(1004, "Wi-Fi accept loop error", e))
                updateState(ITransport.TransportState.ERROR)
            }
        }
    }

    private suspend fun readLoop(socket: Socket) {
        try {
            val reader = BufferedReader(
                InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8)
            )

            while (!socket.isClosed) {
                val line = reader.readLine() ?: break
                if (line.isBlank()) {
                    continue
                }

                val payload = line.toByteArray(StandardCharsets.UTF_8)
                _incomingData.emit(payload)
                logger.debug(tag, "Received ${payload.size} bytes from Wi-Fi client")
            }
        } catch (e: Exception) {
            if (serverSocket != null) {
                logger.error(tag, "Wi-Fi receive loop error", e)
                emitError(ITransport.TransportError(1005, "Wi-Fi receive loop error", e))
            }
        } finally {
            closeClientSocket()
            if (serverSocket != null) {
                updateState(ITransport.TransportState.CONNECTING)
                logger.info(tag, "Wi-Fi client disconnected, listener still active on ${getServerEndpoint()}")
            }
        }
    }

    private fun closeClientSocket() {
        clientSocket?.runCatching { close() }
        clientSocket = null
    }

    private fun closeControlListener() {
        closeClientSocket()
        serverSocket?.runCatching { close() }
        serverSocket = null
    }

    private fun clearActiveGroupState() {
        activeLocalAddress = null
        activeP2pAddress = null
        activeP2pInterfaceName = null
        activeNetworkName = ""
        activePassphrase = ""
    }

    private fun finishExplicitDisconnect() {
        clearActiveGroupState()
        updateState(ITransport.TransportState.DISCONNECTED)
        logger.info(tag, "[NETRELAY-WFD] Wi-Fi Direct stopped")
    }

    private fun parsePort(target: String): Int {
        val trimmed = target.trim()
        if (trimmed.isBlank()) {
            return DEFAULT_PORT
        }

        val portToken = trimmed.substringAfter(':', trimmed)
        return portToken.toIntOrNull()?.takeIf { it in 1..65535 } ?: DEFAULT_PORT
    }

    private suspend fun prepareWifiDirectGroup() {
        val locationEnabled = isLocationServiceEnabled()
        val p2pEnabled = isWifiP2pEnabled()
        logger.info(tag, "[NETRELAY-WFD] Location enabled=$locationEnabled")
        logger.info(tag, "[NETRELAY-WFD] WiFi P2P enabled=$p2pEnabled")

        if (manager == null || channel == null) {
            throw IllegalStateException("Wi-Fi Direct manager/channel unavailable")
        }

        if (!hasWifiDirectRuntimePermission()) {
            throw SecurityException("Wi-Fi Direct runtime permission is missing")
        }

        if (!locationEnabled) {
            val message = "Wi-Fi Direct requires Android Location service to be enabled"
            logger.error(tag, message)
            throw IllegalStateException(message)
        }
        if (!p2pEnabled) {
            throw IllegalStateException("Wi-Fi is disabled; Wi-Fi Direct group cannot be created")
        }

        if (recreateGroupOnNextConnect) {
            logger.info(tag, "Recreating Wi-Fi Direct group to apply saved name/password")
            removeWifiDirectGroupSafely()
            recreateGroupOnNextConnect = false
            delay(500)
        }

        val existingInfo = requestConnectionInfoSafely()
        logger.info(tag, "[NETRELAY-WFD] groupFormed=${existingInfo?.groupFormed == true}")
        logger.info(tag, "[NETRELAY-WFD] isGroupOwner=${existingInfo?.isGroupOwner == true}")
        if (existingInfo?.groupFormed == true) {
            val existingGroup = requestGroupInfoSafely()
            if (existingGroup != null) {
                rememberGroupDetails(existingGroup)
            }

            if (
                existingInfo.isGroupOwner &&
                Build.VERSION.SDK_INT < Build.VERSION_CODES.Q ||
                existingInfo.isGroupOwner && existingGroup?.networkName == configuredNetworkName
            ) {
                rememberP2pAddress(existingInfo, "existing")
                return
            }

            logger.info(
                tag,
                "Existing Wi-Fi Direct group uses ${existingGroup?.networkName ?: "unknown"}; recreating for $configuredNetworkName"
            )
            removeWifiDirectGroupSafely()
            delay(500)
        }

        for (createAttempt in 1..3) {
            logger.info(tag, "[NETRELAY-WFD] Creating P2P group")
            logger.info(tag, "Creating autonomous Wi-Fi Direct group for Windows client, attempt $createAttempt")
            if (createWifiDirectGroupSafely()) {
                if (waitForGroupInfo("created")) {
                    return
                }
            }

            val busyInfo = requestConnectionInfoSafely()
            if (busyInfo?.groupFormed == true) {
                rememberP2pAddress(busyInfo, "busy-existing")
                return
            }

            delay(1000L * createAttempt)
        }

        val message = "Wi-Fi Direct group creation completed without groupFormed=true"
        logger.error(tag, message)
        throw IllegalStateException(message)
    }

    private suspend fun waitForGroupInfo(source: String): Boolean {
        logger.info(tag, "[NETRELAY-WFD] Waiting for group info")
        for (attempt in 1..8) {
            delay(500)
            val info = requestConnectionInfoSafely()
            logger.info(tag, "[NETRELAY-WFD] groupFormed=${info?.groupFormed == true}")
            logger.info(tag, "[NETRELAY-WFD] isGroupOwner=${info?.isGroupOwner == true}")
            if (info?.groupFormed == true) {
                val group = requestGroupInfoSafely()
                if (group == null) {
                    logger.debug(tag, "[NETRELAY-WFD] WifiP2pGroup details not available yet")
                    continue
                }
                rememberGroupDetails(group)
                if (activeNetworkName.isBlank() || activePassphrase.isBlank()) {
                    logger.warn(tag, "[NETRELAY-WFD] Group formed but real SSID/passphrase are unavailable")
                    continue
                }
                rememberP2pAddress(info, source)
                return true
            }
            logger.debug(tag, "Waiting for Wi-Fi Direct group info, attempt $attempt")
        }

        return false
    }

    private fun hasWifiDirectRuntimePermission(): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) {
            return true
        }

        val permission = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            Manifest.permission.NEARBY_WIFI_DEVICES
        } else {
            Manifest.permission.ACCESS_FINE_LOCATION
        }

        return context.checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED
    }

    private fun isLocationServiceEnabled(): Boolean {
        val locationManager = context.getSystemService(Context.LOCATION_SERVICE) as? LocationManager
            ?: return false

        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            locationManager.isLocationEnabled
        } else {
            locationManager.isProviderEnabled(LocationManager.GPS_PROVIDER) ||
                locationManager.isProviderEnabled(LocationManager.NETWORK_PROVIDER)
        }
    }

    private fun isWifiP2pEnabled(): Boolean {
        val wifiManager = context.applicationContext
            .getSystemService(Context.WIFI_SERVICE) as? WifiManager
        return manager != null && channel != null && wifiManager?.isWifiEnabled == true
    }

    private suspend fun requestConnectionInfoSafely(): WifiP2pInfo? {
        val wifiManager = manager ?: return null
        val wifiChannel = channel ?: return null

        return try {
            suspendCancellableCoroutine { continuation ->
                wifiManager.requestConnectionInfo(wifiChannel) { info ->
                    if (continuation.isActive) {
                        continuation.resume(info)
                    }
                }
            }
        } catch (e: SecurityException) {
            logger.warn(tag, "Wi-Fi Direct connection info denied", e)
            null
        } catch (e: Exception) {
            logger.warn(tag, "Wi-Fi Direct connection info unavailable", e)
            null
        }
    }

    private suspend fun createWifiDirectGroupSafely(): Boolean {
        val wifiManager = manager ?: return false
        val wifiChannel = channel ?: return false

        return try {
            suspendCancellableCoroutine { continuation ->
                val listener = object : WifiP2pManager.ActionListener {
                    override fun onSuccess() {
                        logger.info(tag, "[NETRELAY-WFD] createGroup result=SUCCESS")
                        logger.info(
                            tag,
                            "Wi-Fi Direct group creation accepted for $configuredNetworkName"
                        )
                        if (continuation.isActive) {
                            continuation.resume(true)
                        }
                    }

                    override fun onFailure(reasonCode: Int) {
                        logger.warn(tag, "[NETRELAY-WFD] createGroup result=${reasonToText(reasonCode)}")
                        logger.warn(
                            tag,
                            "Wi-Fi Direct group creation failed: ${reasonToText(reasonCode)}"
                        )
                        if (continuation.isActive) {
                            continuation.resume(false)
                        }
                    }
                }

                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                    val config = WifiP2pConfig.Builder()
                        .setNetworkName(configuredNetworkName)
                        .setPassphrase(configuredPassphrase)
                        .build()
                    wifiManager.createGroup(wifiChannel, config, listener)
                } else {
                    wifiManager.createGroup(wifiChannel, listener)
                }
            }
        } catch (e: SecurityException) {
            logger.warn(tag, "Wi-Fi Direct group creation denied", e)
            false
        } catch (e: Exception) {
            logger.warn(tag, "Wi-Fi Direct group creation error", e)
            false
        }
    }

    private suspend fun requestGroupInfoSafely(): WifiP2pGroup? {
        val wifiManager = manager ?: return null
        val wifiChannel = channel ?: return null

        return try {
            suspendCancellableCoroutine { continuation ->
                wifiManager.requestGroupInfo(wifiChannel) { group ->
                    if (continuation.isActive) {
                        continuation.resume(group)
                    }
                }
            }
        } catch (e: SecurityException) {
            logger.warn(tag, "Wi-Fi Direct group info denied", e)
            null
        } catch (e: Exception) {
            logger.warn(tag, "Wi-Fi Direct group info unavailable", e)
            null
        }
    }

    private suspend fun removeWifiDirectGroupSafely(): Boolean {
        val wifiManager = manager ?: return false
        val wifiChannel = channel ?: return false

        return try {
            suspendCancellableCoroutine { continuation ->
                wifiManager.removeGroup(wifiChannel, object : WifiP2pManager.ActionListener {
                    override fun onSuccess() {
                        logger.info(tag, "Wi-Fi Direct group removed")
                        if (continuation.isActive) {
                            continuation.resume(true)
                        }
                    }

                    override fun onFailure(reasonCode: Int) {
                        logger.warn(
                            tag,
                            "Wi-Fi Direct group remove failed: ${reasonToText(reasonCode)}"
                        )
                        if (continuation.isActive) {
                            continuation.resume(false)
                        }
                    }
                })
            }
        } catch (e: SecurityException) {
            logger.warn(tag, "Wi-Fi Direct group remove denied", e)
            false
        } catch (e: Exception) {
            logger.warn(tag, "Wi-Fi Direct group remove error", e)
            false
        }
    }

    private suspend fun stopPeerDiscoverySafely(): Boolean {
        val wifiManager = manager ?: return true
        val wifiChannel = channel ?: return true

        val result = withTimeoutOrNull(P2P_ACTION_TIMEOUT_MS) {
            suspendCancellableCoroutine { continuation ->
                wifiManager.stopPeerDiscovery(wifiChannel, object : WifiP2pManager.ActionListener {
                    override fun onSuccess() {
                        if (continuation.isActive) {
                            continuation.resume(true)
                        }
                    }

                    override fun onFailure(reasonCode: Int) {
                        logger.warn(
                            tag,
                            "[NETRELAY-WFD] stopPeerDiscovery failed: ${reasonToText(reasonCode)}"
                        )
                        if (continuation.isActive) {
                            continuation.resume(false)
                        }
                    }
                })
            }
        }

        if (result == null) {
            logger.warn(tag, "[NETRELAY-WFD] stopPeerDiscovery timed out")
        }
        return result == true
    }

    private suspend fun removeWifiDirectGroupForDisconnectSafely(): Boolean {
        val wifiManager = manager ?: run {
            logger.warn(tag, "[NETRELAY-WFD] removeGroup failed: manager unavailable")
            return false
        }
        val wifiChannel = channel ?: run {
            logger.warn(tag, "[NETRELAY-WFD] removeGroup failed: channel unavailable")
            return false
        }

        val result = withTimeoutOrNull(P2P_ACTION_TIMEOUT_MS) {
            suspendCancellableCoroutine { continuation ->
                wifiManager.removeGroup(wifiChannel, object : WifiP2pManager.ActionListener {
                    override fun onSuccess() {
                        logger.info(tag, "[NETRELAY-WFD] removeGroup success")
                        if (continuation.isActive) {
                            continuation.resume(true)
                        }
                    }

                    override fun onFailure(reasonCode: Int) {
                        logger.warn(
                            tag,
                            "[NETRELAY-WFD] removeGroup failed: ${reasonToText(reasonCode)}"
                        )
                        if (continuation.isActive) {
                            continuation.resume(false)
                        }
                    }
                })
            }
        }

        if (result == null) {
            logger.warn(tag, "[NETRELAY-WFD] removeGroup failed: TIMEOUT")
        }
        return result == true
    }

    private suspend fun verifyGroupRemoved(): Boolean {
        var verified = false
        withTimeoutOrNull(GROUP_REMOVAL_VERIFY_TIMEOUT_MS) {
            while (!verified) {
                currentCoroutineContext().ensureActive()
                val info = withTimeoutOrNull(GROUP_INFO_CALLBACK_TIMEOUT_MS) {
                    requestConnectionInfoSafely()
                }
                if (info != null && !info.groupFormed) {
                    verified = true
                    continue
                }
                delay(GROUP_REMOVAL_VERIFY_INTERVAL_MS)
            }
        }
        return verified
    }

    private fun rememberGroupDetails(group: WifiP2pGroup) {
        val networkName = group.networkName?.trim().orEmpty()
        val passphrase = group.passphrase?.trim().orEmpty()
        val interfaceName = group.`interface`?.trim().orEmpty()

        if (networkName.isNotBlank()) {
            activeNetworkName = networkName
        }

        if (passphrase.isNotBlank()) {
            activePassphrase = passphrase
        }

        if (interfaceName.isNotBlank() && isP2pInterface(interfaceName)) {
            activeP2pInterfaceName = interfaceName
        }

        logger.info(
            tag,
            "Wi-Fi Direct group details: name=${activeNetworkName.ifBlank { "unknown" }} passphrase=${maskPassphrase(activePassphrase)}"
        )
        logger.info(tag, "[NETRELAY-WFD] interface=${group.`interface` ?: "unknown"}")
        logger.info(tag, "[NETRELAY-WFD] networkName=${activeNetworkName.ifBlank { "unknown" }} source=WifiP2pGroup")
        logger.info(tag, "[NETRELAY-WFD] passphrase available=${activePassphrase.isNotBlank()} source=WifiP2pGroup")
    }

    private suspend fun rememberP2pAddress(info: WifiP2pInfo, source: String) {
        val groupOwnerAddress = info.groupOwnerAddress?.hostAddress
        logger.info(
            tag,
            "Wi-Fi Direct $source group ready; owner=${groupOwnerAddress ?: "unknown"}, isOwner=${info.isGroupOwner}"
        )
        logger.info(tag, "[NETRELAY-WFD] ownerAddress=${groupOwnerAddress ?: "unknown"}")
        logger.info(tag, "[NETRELAY-WFD] Waiting for P2P interface address")

        repeat(P2P_ADDRESS_MAX_ATTEMPTS) { index ->
            currentCoroutineContext().ensureActive()
            val attempt = index + 1
            val resolvedAddress = if (
                info.isGroupOwner &&
                !groupOwnerAddress.isNullOrBlank() &&
                isUsableListenerAddress(groupOwnerAddress)
            ) {
                groupOwnerAddress
            } else {
                resolveIpv4Address().takeIf { isUsableListenerAddress(it) }
            }

            logger.info(
                tag,
                "[NETRELAY-WFD] P2P address attempt $attempt: ${resolvedAddress ?: "unavailable"}"
            )

            if (resolvedAddress != null) {
                activeP2pAddress = resolvedAddress
                logger.info(tag, "[NETRELAY-WFD] localP2pAddress=$resolvedAddress")
                logger.info(tag, "[NETRELAY-WFD] P2P address ready=$resolvedAddress")
                return
            }

            delay(P2P_ADDRESS_RETRY_INTERVAL_MS)
        }

        val message =
            "Wi-Fi Direct group formed but no local P2P address appeared within ${P2P_ADDRESS_WAIT_TIMEOUT_MS}ms"
        logger.error(tag, "[NETRELAY-WFD] $message")
        throw IllegalStateException(message)
    }

    private fun resolveIpv4Address(): String {
        return try {
            val expectedInterfaceName = activeP2pInterfaceName
            NetworkInterface.getNetworkInterfaces().toList()
                .asSequence()
                .filter { networkInterface ->
                    networkInterface.isUp &&
                        !networkInterface.isLoopback &&
                        isP2pInterface(networkInterface.name) &&
                        (
                            expectedInterfaceName == null ||
                                networkInterface.name.equals(expectedInterfaceName, ignoreCase = true)
                            )
                }
                .flatMap { networkInterface ->
                    networkInterface.inetAddresses.toList()
                        .filterIsInstance<Inet4Address>()
                        .mapNotNull { address ->
                            val hostAddress = address.hostAddress.orEmpty()
                            if (
                                hostAddress.isBlank() ||
                                hostAddress == "127.0.0.1" ||
                                hostAddress.startsWith("169.254.")
                            ) {
                                null
                            } else {
                                ScoredAddress(
                                    interfaceName = networkInterface.name,
                                    address = hostAddress,
                                    score = scoreAddress(networkInterface.name, hostAddress)
                                )
                            }
                        }
                        .asSequence()
                }
                .sortedBy { it.score }
                .firstOrNull()
                ?.also { candidate ->
                    logger.debug(
                        tag,
                        "Resolved listener IP ${candidate.address} on interface ${candidate.interfaceName}"
                    )
                }
                ?.address
                ?: "0.0.0.0"
        } catch (e: Exception) {
            logger.error(tag, "Failed to resolve IPv4 address", e)
            "0.0.0.0"
        }
    }

    private fun scoreAddress(interfaceName: String, address: String): Int {
        val normalizedName = interfaceName.lowercase()
        val interfaceScore = when {
            normalizedName.contains("ap") -> 0
            normalizedName.contains("p2p") -> 1
            normalizedName.contains("wlan") || normalizedName.contains("wifi") -> 2
            normalizedName.contains("rndis") || normalizedName.contains("usb") -> 3
            else -> 5
        }

        val rangeScore = when {
            address.startsWith("192.168.49.") -> -1
            address.startsWith("192.168.") -> 0
            address.startsWith("10.") -> 1
            isPrivate172Address(address) -> 2
            else -> 4
        }

        return (interfaceScore * 10) + rangeScore
    }

    private fun isPrivate172Address(address: String): Boolean {
        val octets = address.split('.')
        if (octets.size < 2 || octets[0] != "172") {
            return false
        }

        val secondOctet = octets[1].toIntOrNull() ?: return false
        return secondOctet in 16..31
    }

    private fun isUsableListenerAddress(address: String): Boolean {
        return address.isNotBlank() &&
            address != "0.0.0.0" &&
            address != "127.0.0.1" &&
            !address.startsWith("169.254.")
    }

    private fun isP2pInterface(interfaceName: String): Boolean {
        val normalizedName = interfaceName.lowercase()
        return normalizedName.contains("p2p") &&
            !normalizedName.startsWith("rmnet") &&
            !normalizedName.contains("cell") &&
            !normalizedName.contains("mobile")
    }

    private fun buildDefaultNetworkName(): String {
        val model = sanitizeNetworkToken(Build.MODEL).ifBlank { "Android" }
        val base = "DIRECT-NR-$model-NetRelay"
        return base.take(MAX_NETWORK_NAME_LENGTH)
    }

    private fun normalizeNetworkName(value: String): String? {
        val trimmed = value.trim()
        if (trimmed.isBlank()) {
            return null
        }

        val prefixed = if (trimmed.startsWith("DIRECT-", ignoreCase = false)) {
            trimmed
        } else {
            "DIRECT-NR-${sanitizeNetworkToken(trimmed)}"
        }

        val sanitized = sanitizeNetworkName(prefixed)
            .take(MAX_NETWORK_NAME_LENGTH)
            .trimEnd('-', '_')

        return sanitized.takeIf {
            it.startsWith("DIRECT-", ignoreCase = false) &&
                it.length in MIN_NETWORK_NAME_LENGTH..MAX_NETWORK_NAME_LENGTH &&
                it.drop("DIRECT-".length).length >= 2
        }
    }

    private fun sanitizeNetworkName(value: String): String {
        return value.filter { character ->
            character.code in 32..126 && (
                character.isLetterOrDigit() ||
                    character == '-' ||
                    character == '_' ||
                    character == '.'
            )
        }
    }

    private fun sanitizeNetworkToken(value: String): String {
        return sanitizeNetworkName(value)
            .replace('.', '-')
            .ifBlank { "Android" }
    }

    private fun isValidPassphrase(value: String): Boolean {
        return value.length in MIN_PASSPHRASE_LENGTH..MAX_PASSPHRASE_LENGTH &&
            value.all { character -> character.code in 32..126 }
    }

    private fun maskPassphrase(value: String): String {
        if (value.isBlank()) {
            return "<empty>"
        }

        return "${value.take(2)}***${value.takeLast(2)}"
    }

    private fun reasonToText(reasonCode: Int): String {
        return when (reasonCode) {
            WifiP2pManager.P2P_UNSUPPORTED -> "P2P_UNSUPPORTED"
            WifiP2pManager.BUSY -> "BUSY"
            WifiP2pManager.ERROR -> "ERROR"
            else -> "reason=$reasonCode"
        }
    }

    private fun updateState(state: ITransport.TransportState) {
        currentState = state
        _stateFlow.value = state
        logger.debug(tag, "State changed to: $state")
    }

    private fun emitError(error: ITransport.TransportError) {
        _errorFlow.tryEmit(error)
        logger.error(tag, "Transport error ${error.code}: ${error.message}", error.cause)
    }

    companion object {
        const val DEFAULT_PORT = 8988
        private const val PREFERENCES_NAME = "wifi_direct_settings"
        private const val KEY_NETWORK_NAME = "network_name"
        private const val KEY_PASSPHRASE = "passphrase"
        private const val DEFAULT_PASSPHRASE = "netrelay11"
        private const val MIN_NETWORK_NAME_LENGTH = 9
        private const val MAX_NETWORK_NAME_LENGTH = 32
        private const val MIN_PASSPHRASE_LENGTH = 8
        private const val MAX_PASSPHRASE_LENGTH = 63
        private const val P2P_ADDRESS_RETRY_INTERVAL_MS = 100L
        private const val P2P_ADDRESS_WAIT_TIMEOUT_MS = 5_000L
        private const val P2P_ADDRESS_MAX_ATTEMPTS = 50
        private const val P2P_ACTION_TIMEOUT_MS = 5_000L
        private const val GROUP_INFO_CALLBACK_TIMEOUT_MS = 1_000L
        private const val GROUP_REMOVAL_VERIFY_INTERVAL_MS = 150L
        private const val GROUP_REMOVAL_VERIFY_TIMEOUT_MS = 4_000L
    }

    private data class ScoredAddress(
        val interfaceName: String,
        val address: String,
        val score: Int
    )
}
