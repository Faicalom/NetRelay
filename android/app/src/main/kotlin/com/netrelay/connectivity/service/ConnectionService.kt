package com.netrelay.connectivity.service

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Binder
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.netrelay.connectivity.R
import com.netrelay.connectivity.domain.Device
import com.netrelay.connectivity.gateway.SocketGatewayServer
import com.netrelay.connectivity.protocol.MessageSerializer
import com.netrelay.connectivity.protocol.MessageTypes
import com.netrelay.connectivity.protocol.ProtocolHandler
import com.netrelay.connectivity.protocol.ProtocolMessage
import com.netrelay.connectivity.transport.ITransport
import com.netrelay.connectivity.transport.TransportFactory
import com.netrelay.connectivity.transport.usb.UsbTransport
import com.netrelay.connectivity.transport.wifi.WifiDirectTransport
import com.netrelay.connectivity.util.Logger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/**
 * Foreground service for managing background connectivity and proxy availability.
 */
class ConnectionService : Service() {

    private val logger = Logger
    private val tag = "ConnectionService"

    private val binder = LocalBinder()
    private val serviceScope = CoroutineScope(Dispatchers.Default + Job())

    private val transportManager = TransportManager()
    private val sessionManager = SessionManager()
    private val protocolHandler = ProtocolHandler(MessageSerializer())
    private val socketGatewayServer by lazy { SocketGatewayServer(applicationContext) }
    private var userStopRequested = false
    private val transportStartMutex = Mutex()

    @Volatile
    private var activeServerMode: String? = null

    inner class LocalBinder : Binder() {
        fun getService(): ConnectionService = this@ConnectionService
    }

    override fun onCreate() {
        super.onCreate()
        logger.info(tag, "Service created")
        ConnectionRuntimeStore.resetTransportUiStates()
        ConnectionRuntimeStore.updateServiceState(getString(R.string.service_state_starting))
        registerTransports()
        registerProtocolHandlers()
        observeServerTransport(WIFI_TRANSPORT)
        observeServerTransport(USB_TRANSPORT)
        showForegroundNotification(ConnectionRuntimeStore.selectedMode.value, getString(R.string.service_state_starting))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        logger.info(tag, "Service started")
        if (intent?.action == ACTION_STOP_SERVICE) {
            requestStopService()
            return START_NOT_STICKY
        }

        val prefs = getSharedPreferences(SERVICE_PREFS, Context.MODE_PRIVATE)
        val wasStartedByUser = prefs.getBoolean(KEY_SERVICE_ACTIVE, false)
        if (intent == null && !wasStartedByUser) {
            stopSelf()
            return START_NOT_STICKY
        }

        val requestedMode = intent?.getStringExtra(EXTRA_TRANSPORT_MODE)
            ?: prefs.getString(KEY_ACTIVE_MODE, ConnectionRuntimeStore.selectedMode.value)
            ?: ConnectionRuntimeStore.selectedMode.value
        if (ConnectionRuntimeStore.transportUiState(requestedMode) != TransportUiState.ACTIVE) {
            ConnectionRuntimeStore.markTransportActivating(requestedMode)
        }
        userStopRequested = false
        prefs.edit()
            .putBoolean(KEY_SERVICE_ACTIVE, true)
            .putString(KEY_ACTIVE_MODE, requestedMode)
            .apply()
        showForegroundNotification(requestedMode, waitingMessageFor(requestedMode))
        val wifiSettingsChanged = applyRequestedWifiSettings(intent)
        if (wifiSettingsChanged && requestedMode == WIFI_TRANSPORT) {
            ConnectionRuntimeStore.updateSelectedMode(WIFI_TRANSPORT)
            serviceScope.launch {
                (transportManager.getTransport(WIFI_TRANSPORT) as? WifiDirectTransport)?.disconnect()
                startServerTransport(WIFI_TRANSPORT)
            }
        } else {
            applyRequestedMode(requestedMode)
        }
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder = binder

    override fun onUnbind(intent: Intent?): Boolean {
        logger.debug(tag, "Service unbound")
        return true
    }

    override fun onDestroy() {
        logger.info(tag, "Service destroyed")
        if (userStopRequested) {
            ConnectionRuntimeStore.reset()
        }
        serviceScope.launch {
            socketGatewayServer.cleanup()
            transportManager.cleanup()
            sessionManager.closeSession()
        }
        super.onDestroy()
    }

    override fun onTaskRemoved(rootIntent: Intent?) {
        super.onTaskRemoved(rootIntent)
        if (userStopRequested) {
            return
        }

        val prefs = getSharedPreferences(SERVICE_PREFS, Context.MODE_PRIVATE)
        if (!prefs.getBoolean(KEY_SERVICE_ACTIVE, false)) {
            return
        }

        val restartMode = prefs.getString(KEY_ACTIVE_MODE, ConnectionRuntimeStore.selectedMode.value)
            ?: ConnectionRuntimeStore.selectedMode.value
        val restartIntent = Intent(applicationContext, ConnectionService::class.java).apply {
            putExtra(EXTRA_TRANSPORT_MODE, restartMode)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            applicationContext.startForegroundService(restartIntent)
        } else {
            applicationContext.startService(restartIntent)
        }
    }

    private fun requestStopService() {
        if (userStopRequested) {
            logger.info(tag, "Explicit disconnect already in progress")
            return
        }

        userStopRequested = true
        getSharedPreferences(SERVICE_PREFS, Context.MODE_PRIVATE)
            .edit()
            .putBoolean(KEY_SERVICE_ACTIVE, false)
            .remove(KEY_ACTIVE_MODE)
            .apply()

        serviceScope.launch {
            try {
                stopServerTransportsForUserDisconnect()
            } catch (e: Exception) {
                logger.error(tag, "Explicit disconnect cleanup failed", e)
            } finally {
                ConnectionRuntimeStore.reset()
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                    stopForeground(STOP_FOREGROUND_REMOVE)
                } else {
                    @Suppress("DEPRECATION")
                    stopForeground(true)
                }
                stopSelf()
            }
        }
    }

    private fun showForegroundNotification(transportType: String, message: String) {
        createNotificationChannels()

        val openIntent = PendingIntent.getActivity(
            this,
            10,
            Intent(this, com.netrelay.connectivity.presentation.MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val stopIntent = PendingIntent.getService(
            this,
            11,
            Intent(this, ConnectionService::class.java).setAction(ACTION_STOP_SERVICE),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        val notification = NotificationCompat.Builder(this, channelIdFor(transportType))
            .setContentTitle(notificationTitleFor(transportType))
            .setContentText(message)
            .setSmallIcon(R.drawable.ic_netrelay_notification)
            .setContentIntent(openIntent)
            .addAction(android.R.drawable.ic_menu_close_clear_cancel, getString(R.string.button_stop_service), stopIntent)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .build()

        startForeground(notificationIdFor(transportType), notification)
        cancelInactiveNotifications(transportType)
        logger.debug(tag, "Foreground notification updated for $transportType")
    }

    private fun createNotificationChannels() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) {
            return
        }

        val manager = getSystemService(NotificationManager::class.java) ?: return
        listOf(WIFI_TRANSPORT, USB_TRANSPORT).forEach { transportType ->
            val channel = NotificationChannel(
                channelIdFor(transportType),
                notificationTitleFor(transportType),
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = getString(R.string.connection_service_channel_description)
                setShowBadge(false)
            }
            manager.createNotificationChannel(channel)
        }
    }

    private fun cancelInactiveNotifications(activeTransport: String) {
        val manager = getSystemService(NotificationManager::class.java) ?: return
        listOf(WIFI_TRANSPORT, USB_TRANSPORT)
            .filterNot { it == activeTransport }
            .forEach { manager.cancel(notificationIdFor(it)) }
    }

    private fun channelIdFor(transportType: String): String {
        return when (transportType) {
            USB_TRANSPORT -> USB_CHANNEL_ID
            else -> WIFI_CHANNEL_ID
        }
    }

    private fun notificationIdFor(transportType: String): Int {
        return when (transportType) {
            USB_TRANSPORT -> USB_NOTIFICATION_ID
            else -> WIFI_NOTIFICATION_ID
        }
    }

    private fun notificationIconFor(transportType: String): Int {
        return when (transportType) {
            USB_TRANSPORT -> R.drawable.ic_usb_24
            else -> R.drawable.ic_wifi_24
        }
    }

    private fun notificationTitleFor(transportType: String): String {
        return when (transportType) {
            USB_TRANSPORT -> "NetRelay USB"
            else -> "NetRelay Wi-Fi Direct"
        }
    }

    private fun registerTransports() {
        TransportFactory.createDefaultTransports(this).forEach { (transportType, transport) ->
            transportManager.registerTransport(transportType, transport)
        }
        logger.info(
            tag,
            "Registered service transports: ${transportManager.getAvailableTransports().joinToString()}"
        )
    }

    private fun applyRequestedMode(mode: String) {
        ConnectionRuntimeStore.updateSelectedMode(mode)
        when (mode) {
            WIFI_TRANSPORT -> startServerTransport(WIFI_TRANSPORT)
            USB_TRANSPORT -> startServerTransport(USB_TRANSPORT)
            else -> {
                stopServerTransports()
                ConnectionRuntimeStore.updateServiceState(
                    getString(R.string.status_error, getString(R.string.mode_unknown))
                )
            }
        }
    }

    private fun applyRequestedWifiSettings(intent: Intent?): Boolean {
        val requestedNetworkName = intent?.getStringExtra(EXTRA_WIFI_NETWORK_NAME)
        val requestedPassphrase = intent?.getStringExtra(EXTRA_WIFI_PASSPHRASE)
        val wifiTransport = transportManager.getTransport(WIFI_TRANSPORT) as? WifiDirectTransport
            ?: return false

        if (requestedNetworkName.isNullOrBlank() && requestedPassphrase.isNullOrBlank()) {
            updateWifiDirectRuntimeInfo(wifiTransport)
            return false
        }

        val passphrase = requestedPassphrase?.trim()
            ?.takeIf { it.isNotBlank() }
            ?: wifiTransport.getWifiDirectPassphrase()

        val updated = wifiTransport.updateWifiDirectSettings(requestedNetworkName, passphrase)
        updateWifiDirectRuntimeInfo(wifiTransport)

        if (!updated) {
            ConnectionRuntimeStore.updateLastProtocolMessage(
                getString(R.string.wifi_passphrase_invalid)
            )
        }

        return updated
    }

    private fun updateWifiDirectRuntimeInfo(transport: WifiDirectTransport? = null) {
        val wifiTransport = transport ?: transportManager.getTransport(WIFI_TRANSPORT) as? WifiDirectTransport
            ?: return

        val networkName = wifiTransport.getWifiDirectNetworkName()
        val passphrase = wifiTransport.getWifiDirectPassphrase()
        ConnectionRuntimeStore.updateWifiNetworkName(
            networkName.ifBlank { getString(R.string.endpoint_unavailable) }
        )
        ConnectionRuntimeStore.updateWifiPassphrase(
            passphrase.ifBlank { getString(R.string.endpoint_unavailable) }
        )
    }

    private fun clearWifiDirectRuntimeInfo() {
        ConnectionRuntimeStore.updateWifiNetworkName(getString(R.string.endpoint_unavailable))
        ConnectionRuntimeStore.updateWifiPassphrase(getString(R.string.endpoint_unavailable))
    }

    private fun startServerTransport(transportType: String) {
        serviceScope.launch {
            transportStartMutex.withLock {
                val usbTransport = transportManager.getTransport(USB_TRANSPORT) as? UsbTransport
                if (transportType == USB_TRANSPORT &&
                    activeServerMode == USB_TRANSPORT &&
                    usbTransport?.isListening() == true &&
                    socketGatewayServer.isListeningOn("127.0.0.1")
                ) {
                    logger.info(tag, "[NETRELAY-USB] USB mode already active; keeping existing gateway")
                    logger.info(tag, "[NETRELAY-USB] Socket gateway already listening on 127.0.0.1:8991")
                    updateActiveServiceState(USB_TRANSPORT, waitingMessageFor(USB_TRANSPORT))
                    return@withLock
                }

                stopInactiveServerTransports(transportType)

                if (!transportManager.setActiveTransport(transportType)) {
                    logger.error(tag, "Transport unavailable: $transportType")
                    ConnectionRuntimeStore.updateTransportUiState(transportType, TransportUiState.ERROR)
                    updateActiveServiceState(transportType, unavailableMessageFor(transportType))
                    return@withLock
                }

                val listening = transportManager.connect(listeningTargetFor(transportType))
                val endpoint = getListenerEndpointFor(transportType)
                if (!listening || endpoint == null) {
                    logger.error(tag, "Failed to start transport listener: $transportType")
                    ConnectionRuntimeStore.updateTransportUiState(transportType, TransportUiState.ERROR)
                    updateActiveServiceState(transportType, failedMessageFor(transportType))
                    ConnectionRuntimeStore.updateProxyEndpoint(getString(R.string.proxy_endpoint_unavailable))
                    ConnectionRuntimeStore.updateProxyStatus(getString(R.string.proxy_status_error))
                    return@withLock
                }

                logger.info(tag, "Server transport ready on $endpoint for mode $transportType")
                if (transportType == WIFI_TRANSPORT) {
                    logger.info(tag, "[NETRELAY-WFD] Control listening on $endpoint")
                    ConnectionRuntimeStore.updateTransportUiState(
                        WIFI_TRANSPORT,
                        if ((transportManager.getTransport(WIFI_TRANSPORT) as? WifiDirectTransport)?.hasActiveGroup() == true) {
                            TransportUiState.ACTIVE
                        } else {
                            TransportUiState.ERROR
                        }
                    )
                }
                updateActiveServiceState(transportType, waitingMessageFor(transportType))
                ConnectionRuntimeStore.updateWifiListenerEndpoint(endpoint)
                if (transportType == WIFI_TRANSPORT) {
                    updateWifiDirectRuntimeInfo()
                }
                ConnectionRuntimeStore.updateWifiClientEndpoint(getString(R.string.client_disconnected))
                if (!startProxyForTransport(transportType, endpoint)) {
                    logger.error(tag, "Failed to start data plane for transport: $transportType")
                    ConnectionRuntimeStore.updateTransportUiState(transportType, TransportUiState.ERROR)
                    updateActiveServiceState(transportType, failedMessageFor(transportType))
                    return@withLock
                }
                activeServerMode = transportType
                if (transportType == USB_TRANSPORT && socketGatewayServer.isListeningOn("127.0.0.1")) {
                    logger.info(tag, "[NETRELAY-USB] Gateway listening 127.0.0.1:8991")
                }
            }
        }
    }

    private fun stopServerTransports() {
        serviceScope.launch {
            stopInactiveServerTransports(activeTransport = null)
            ConnectionRuntimeStore.resetTransportUiStates()
            clearWifiDirectRuntimeInfo()
            ConnectionRuntimeStore.updateWifiClientEndpoint(getString(R.string.client_disconnected))
            ConnectionRuntimeStore.updateWifiListenerEndpoint(getString(R.string.endpoint_unavailable))
            ConnectionRuntimeStore.updateProxyEndpoint(getString(R.string.proxy_endpoint_unavailable))
            ConnectionRuntimeStore.updateProxyStatus(getString(R.string.proxy_status_stopped))
            ConnectionRuntimeStore.updateGatewayEndpoint(getString(R.string.proxy_endpoint_unavailable))
            ConnectionRuntimeStore.updateGatewayStatus(getString(R.string.proxy_status_stopped))
        }
    }

    private suspend fun stopInactiveServerTransports(activeTransport: String?) {
        activeServerMode = null
        listOf(WIFI_TRANSPORT, USB_TRANSPORT)
            .filter { it != activeTransport }
            .forEach { transportType ->
                when (val transport = transportManager.getTransport(transportType)) {
                    is WifiDirectTransport -> transport.disconnect()
                    is UsbTransport -> transport.disconnect()
                }
                ConnectionRuntimeStore.updateTransportUiState(transportType, TransportUiState.INACTIVE)
                if (transportType == WIFI_TRANSPORT) {
                    clearWifiDirectRuntimeInfo()
                }
            }

        socketGatewayServer.stop()
    }

    private suspend fun stopServerTransportsForUserDisconnect() {
        activeServerMode = null
        val selectedMode = ConnectionRuntimeStore.selectedMode.value

        sessionManager.closeSession("Explicit user disconnect")
        if (selectedMode == WIFI_TRANSPORT) {
            logger.info(tag, "[NETRELAY-WFD] Stopping gateway")
        }

        // The gateway closes its AEL2 client and all TCP/UDP flow sockets before
        // the Wi-Fi Direct control listener and group are removed.
        socketGatewayServer.stop()

        listOf(WIFI_TRANSPORT, USB_TRANSPORT).forEach { transportType ->
            when (val transport = transportManager.getTransport(transportType)) {
                is WifiDirectTransport -> {
                    if (selectedMode == WIFI_TRANSPORT) {
                        transport.disconnectAndRemoveGroup()
                    } else {
                        transport.disconnect()
                    }
                }
                is UsbTransport -> transport.disconnect()
            }
        }

        ConnectionRuntimeStore.updateWifiClientEndpoint(getString(R.string.client_disconnected))
        ConnectionRuntimeStore.updateWifiListenerEndpoint(getString(R.string.endpoint_unavailable))
        ConnectionRuntimeStore.updateProxyEndpoint(getString(R.string.proxy_endpoint_unavailable))
        ConnectionRuntimeStore.updateProxyStatus(getString(R.string.proxy_status_stopped))
        ConnectionRuntimeStore.updateGatewayEndpoint(getString(R.string.proxy_endpoint_unavailable))
        ConnectionRuntimeStore.updateGatewayStatus(getString(R.string.proxy_status_stopped))
        ConnectionRuntimeStore.resetTransportUiStates()
        clearWifiDirectRuntimeInfo()
    }

    private fun observeServerTransport(transportType: String) {
        val transport = transportManager.getTransport(transportType) ?: run {
            logger.warn(tag, "Transport not available for observation: $transportType")
            return
        }

        serviceScope.launch {
            transport.observeState().collect { state ->
                val listenerReady = when (transport) {
                    is WifiDirectTransport -> transport.isListening()
                    is UsbTransport -> transport.isListening()
                    else -> false
                }
                val uiState = TransportUiStatePolicy.fromRuntime(
                    mode = transportType,
                    lifecycle = state,
                    listenerReady = listenerReady,
                    clientConnected = transport.isConnected(),
                    wifiDirectGroupReady = (transport as? WifiDirectTransport)?.hasActiveGroup() == true
                )
                ConnectionRuntimeStore.updateTransportUiState(transportType, uiState)
                if (transportType == WIFI_TRANSPORT && uiState != TransportUiState.ACTIVE) {
                    clearWifiDirectRuntimeInfo()
                }

                if (ConnectionRuntimeStore.selectedMode.value != transportType) {
                    return@collect
                }

                val listenerEndpoint = getListenerEndpointFor(transportType)
                if (listenerEndpoint != null) {
                    ConnectionRuntimeStore.updateWifiListenerEndpoint(listenerEndpoint)
                    if (transportType == WIFI_TRANSPORT) {
                        updateWifiDirectRuntimeInfo()
                    }
                    if (socketGatewayServer.isRunning()) {
                        // Wi-Fi Direct can learn its real P2P-local address only after a client connects.
                        // Rebind the data gateway to that concrete address so Windows reaches the
                        // same interface used by the control channel instead of a stale Wi-Fi address.
                        val gatewayReady = socketGatewayServer.start(listenerEndpoint)

                        if (gatewayReady) {
                            val host = listenerEndpoint.substringBefore(':').ifBlank { "127.0.0.1" }
                            val gatewayEndpoint = "ael2://$host:${SocketGatewayServer.DEFAULT_PORT}"
                            ConnectionRuntimeStore.updateGatewayEndpoint(gatewayEndpoint)
                            ConnectionRuntimeStore.updateGatewayStatus(getString(R.string.gateway_status_running))
                        } else {
                            ConnectionRuntimeStore.updateGatewayStatus(getString(R.string.proxy_status_error))
                        }
                    }

                }

                when (state) {
                    ITransport.TransportState.CONNECTING -> {
                        updateActiveServiceState(transportType, waitingMessageFor(transportType))
                        ConnectionRuntimeStore.updateWifiClientEndpoint(getString(R.string.client_disconnected))
                    }
                    ITransport.TransportState.CONNECTED -> {
                        updateActiveServiceState(transportType, connectedMessageFor(transportType))
                        ConnectionRuntimeStore.updateWifiClientEndpoint(
                            getClientEndpointFor(transportType) ?: getString(R.string.client_disconnected)
                        )
                    }
                    ITransport.TransportState.DISCONNECTED -> {
                        updateActiveServiceState(transportType, stoppedMessageFor(transportType))
                        ConnectionRuntimeStore.updateWifiClientEndpoint(getString(R.string.client_disconnected))
                    }
                    ITransport.TransportState.DISCONNECTING -> {
                        updateActiveServiceState(transportType, stoppingMessageFor(transportType))
                    }
                    ITransport.TransportState.ERROR -> {
                        updateActiveServiceState(transportType, errorMessageFor(transportType))
                        ConnectionRuntimeStore.updateProxyStatus(getString(R.string.proxy_status_error))
                    }
                }
            }
        }

        serviceScope.launch {
            transport.observeIncoming().collect { payload ->
                if (ConnectionRuntimeStore.selectedMode.value == transportType) {
                    protocolHandler.processIncoming(payload)
                }
            }
        }

        serviceScope.launch {
            transport.observeErrors().collect { error ->
                if (ConnectionRuntimeStore.selectedMode.value == transportType) {
                    ConnectionRuntimeStore.updateLastProtocolMessage(
                        getString(R.string.protocol_transport_error, error.message)
                    )
                }
            }
        }
    }

    private fun registerProtocolHandlers() {
        protocolHandler.registerMessageHandler(MessageTypes.HANDSHAKE) { message ->
            val activeTransport = ConnectionRuntimeStore.selectedMode.value
            val device = Device(
                deviceId = message.payload["deviceId"] ?: "windows-client",
                deviceName = message.payload["deviceName"] ?: "Windows Client",
                osVersion = message.payload["osVersion"] ?: "Windows",
                appVersion = message.payload["appVersion"] ?: "1.0.0",
                transportType = activeTransport
            )

            sessionManager.initiateSession(device)
            sessionManager.startHandshake()
            sessionManager.markActive()

            val summary = getString(R.string.protocol_handshake_from, device.deviceName)
            logger.info(tag, summary)
            ConnectionRuntimeStore.updateLastProtocolMessage(summary)

            sendProtocolMessage(
                ProtocolMessage(
                    type = MessageTypes.STATUS_UPDATE,
                    sessionId = message.sessionId,
                    payload = mapOf(
                        "status" to "READY",
                        "transport" to activeTransport,
                        "role" to "android-server",
                        "endpoint" to ConnectionRuntimeStore.wifiListenerEndpoint.value,
                        "proxyEndpoint" to ConnectionRuntimeStore.proxyEndpoint.value,
                        "proxyStatus" to ConnectionRuntimeStore.proxyStatus.value,
                        "gatewayEndpoint" to ConnectionRuntimeStore.gatewayEndpoint.value,
                        "socksEndpoint" to ConnectionRuntimeStore.gatewayEndpoint.value,
                        "gatewayStatus" to ConnectionRuntimeStore.gatewayStatus.value,
                        "gatewayReady" to socketGatewayServer.isRunning().toString(),
                        "capabilities" to "gateway.ael2.tcpudp.v1,auto-reconnect.v1",
                        "wifiNetworkName" to ConnectionRuntimeStore.wifiNetworkName.value,
                        "wifiPassphrase" to ConnectionRuntimeStore.wifiPassphrase.value,
                        "deviceName" to Build.MODEL,
                        "deviceId" to Build.DEVICE,
                        "osVersion" to Build.VERSION.RELEASE
                    )
                )
            )
        }

        protocolHandler.registerMessageHandler(MessageTypes.HEARTBEAT) { message ->
            ConnectionRuntimeStore.updateLastProtocolMessage(getString(R.string.protocol_heartbeat_received))
            sendProtocolMessage(
                ProtocolMessage(
                    type = MessageTypes.HEARTBEAT_ACK,
                    sessionId = message.sessionId,
                    payload = mapOf("status" to "ALIVE")
                )
            )
        }

        protocolHandler.registerMessageHandler(MessageTypes.STATUS_QUERY) { message ->
            ConnectionRuntimeStore.updateLastProtocolMessage(getString(R.string.protocol_status_query_received))
            sendProtocolMessage(
                ProtocolMessage(
                    type = MessageTypes.STATUS_UPDATE,
                    sessionId = message.sessionId,
                    payload = mapOf(
                        "status" to "READY",
                        "transport" to ConnectionRuntimeStore.selectedMode.value,
                        "listenerEndpoint" to ConnectionRuntimeStore.wifiListenerEndpoint.value,
                        "client" to ConnectionRuntimeStore.wifiClientEndpoint.value,
                        "proxyEndpoint" to ConnectionRuntimeStore.proxyEndpoint.value,
                        "proxyStatus" to ConnectionRuntimeStore.proxyStatus.value,
                        "gatewayEndpoint" to ConnectionRuntimeStore.gatewayEndpoint.value,
                        "socksEndpoint" to ConnectionRuntimeStore.gatewayEndpoint.value,
                        "gatewayStatus" to ConnectionRuntimeStore.gatewayStatus.value,
                        "gatewayReady" to socketGatewayServer.isRunning().toString(),
                        "capabilities" to "gateway.ael2.tcpudp.v1,auto-reconnect.v1",
                        "wifiNetworkName" to ConnectionRuntimeStore.wifiNetworkName.value,
                        "wifiPassphrase" to ConnectionRuntimeStore.wifiPassphrase.value,
                        "deviceName" to Build.MODEL,
                        "deviceId" to Build.DEVICE,
                        "osVersion" to Build.VERSION.RELEASE
                    )
                )
            )
        }

        protocolHandler.registerMessageHandler(MessageTypes.DISCONNECT) { message ->
            ConnectionRuntimeStore.updateLastProtocolMessage(getString(R.string.protocol_disconnect_requested))
            sendProtocolMessage(
                ProtocolMessage(
                    type = MessageTypes.DISCONNECT_ACK,
                    sessionId = message.sessionId,
                    payload = mapOf("status" to "BYE")
                )
            )
        }
    }

    private suspend fun sendProtocolMessage(message: ProtocolMessage) {
        val bytes = protocolHandler.sendMessage(message)
        if (!transportManager.send(bytes)) {
            logger.error(tag, "Failed to send protocol message: ${message.type}")
        }
    }

    private suspend fun startProxyForTransport(transportType: String, listenerEndpoint: String): Boolean {
        // NetRelay data plane: AEL2 framed TCP/UDP gateway. This is the preferred path.
        val gatewayReady = socketGatewayServer.start(listenerEndpoint)

        if (gatewayReady) {
            val host = listenerEndpoint.substringBefore(':').ifBlank { "0.0.0.0" }
            val gatewayEndpoint = "ael2://$host:${SocketGatewayServer.DEFAULT_PORT}"
            ConnectionRuntimeStore.updateGatewayEndpoint(gatewayEndpoint)
            ConnectionRuntimeStore.updateGatewayStatus(getString(R.string.gateway_status_running))
            logger.info(tag, "Phone v2 gateway ready on $gatewayEndpoint")
            if (transportType == WIFI_TRANSPORT) {
                logger.info(tag, "[NETRELAY-WFD] Gateway listening on $gatewayEndpoint")
            }
        } else {
            ConnectionRuntimeStore.updateGatewayEndpoint(getString(R.string.proxy_endpoint_unavailable))
            ConnectionRuntimeStore.updateGatewayStatus(getString(R.string.proxy_status_error))
            logger.warn(tag, "NetRelay AEL2 gateway unavailable")
        }

        ConnectionRuntimeStore.updateProxyEndpoint(getString(R.string.proxy_endpoint_unavailable))
        ConnectionRuntimeStore.updateProxyStatus(
            if (gatewayReady) getString(R.string.ael2_only) else getString(R.string.proxy_status_error)
        )
        return gatewayReady
    }

    fun connectDevice(deviceId: String, transportType: String) {
        serviceScope.launch {
            logger.info(tag, "Service connecting to device: $deviceId")
            if (!transportManager.setActiveTransport(transportType)) {
                logger.error(tag, "Transport not available: $transportType")
                return@launch
            }

            if (!transportManager.connect(deviceId)) {
                logger.error(tag, "Failed to connect device: $deviceId")
            }
        }
    }

    fun disconnectDevice() {
        serviceScope.launch {
            logger.info(tag, "Service disconnecting from device")
            transportManager.disconnect()
        }
    }

    fun getTransportManager(): TransportManager = transportManager

    fun getSessionManager(): SessionManager = sessionManager

    private fun defaultPortFor(transportType: String): Int {
        return when (transportType) {
            USB_TRANSPORT -> UsbTransport.DEFAULT_PORT
            else -> WifiDirectTransport.DEFAULT_PORT
        }
    }

    private fun listeningTargetFor(transportType: String): String {
        return "listen:${defaultPortFor(transportType)}"
    }

    private fun getListenerEndpointFor(transportType: String): String? {
        return when (val transport = transportManager.getTransport(transportType)) {
            is WifiDirectTransport -> transport.getServerEndpoint()
            is UsbTransport -> transport.getServerEndpoint()
            else -> null
        }
    }

    private fun getClientEndpointFor(transportType: String): String? {
        return when (val transport = transportManager.getTransport(transportType)) {
            is WifiDirectTransport -> transport.getConnectedClientEndpoint()
            is UsbTransport -> transport.getConnectedClientEndpoint()
            else -> null
        }
    }

    private fun waitingMessageFor(transportType: String): String {
        return when (transportType) {
            USB_TRANSPORT -> getString(R.string.service_state_waiting_for_usb_client)
            else -> getString(R.string.service_state_waiting_for_client)
        }
    }

    private fun connectedMessageFor(transportType: String): String {
        return when (transportType) {
            USB_TRANSPORT -> getString(R.string.service_state_usb_connected)
            else -> getString(R.string.service_state_wifi_connected)
        }
    }

    private fun stoppedMessageFor(transportType: String): String {
        return when (transportType) {
            USB_TRANSPORT -> getString(R.string.service_state_usb_stopped)
            else -> getString(R.string.service_state_wifi_stopped)
        }
    }

    private fun stoppingMessageFor(transportType: String): String {
        return when (transportType) {
            USB_TRANSPORT -> getString(R.string.service_state_usb_stopping)
            else -> getString(R.string.service_state_wifi_stopping)
        }
    }

    private fun errorMessageFor(transportType: String): String {
        return when (transportType) {
            USB_TRANSPORT -> getString(R.string.service_state_usb_error)
            else -> getString(R.string.service_state_wifi_error)
        }
    }

    private fun unavailableMessageFor(transportType: String): String {
        return when (transportType) {
            USB_TRANSPORT -> getString(R.string.service_state_usb_unavailable)
            else -> getString(R.string.service_state_wifi_unavailable)
        }
    }

    private fun failedMessageFor(transportType: String): String {
        return when (transportType) {
            USB_TRANSPORT -> getString(R.string.service_state_usb_error)
            else -> getString(R.string.service_state_wifi_failed)
        }
    }

    private fun updateActiveServiceState(transportType: String, message: String) {
        ConnectionRuntimeStore.updateServiceState(message)
        showForegroundNotification(transportType, message)
    }

    companion object {
        const val ACTION_STOP_SERVICE = "com.netrelay.connectivity.action.STOP_SERVICE"
        const val WIFI_CHANNEL_ID = "netrelay_connectivity_wifi_direct"
        const val USB_CHANNEL_ID = "netrelay_connectivity_usb"
        const val WIFI_NOTIFICATION_ID = 1101
        const val USB_NOTIFICATION_ID = 1102
        const val EXTRA_TRANSPORT_MODE = "transport_mode"
        const val EXTRA_WIFI_NETWORK_NAME = "wifi_network_name"
        const val EXTRA_WIFI_PASSPHRASE = "wifi_passphrase"
        private const val SERVICE_PREFS = "netrelay_link_service"
        private const val KEY_ACTIVE_MODE = "active_mode"
        private const val KEY_SERVICE_ACTIVE = "service_active"
        private const val WIFI_TRANSPORT = "wifiDirect"
        private const val USB_TRANSPORT = "usb"
    }
}
