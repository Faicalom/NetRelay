package com.netrelay.connectivity.transport.usb

import com.netrelay.connectivity.transport.ITransport
import com.netrelay.connectivity.util.Logger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.ServerSocket
import java.net.Socket
import java.nio.charset.StandardCharsets

/**
 * USB transport backed by an ADB-forwarded TCP socket.
 * The desktop app forwards local ports over USB, then connects to 127.0.0.1 on the phone.
 */
class UsbTransport : ITransport {

    private val logger = Logger
    private val tag = "UsbTransport"
    private val transportScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val _incomingData = MutableSharedFlow<ByteArray>(extraBufferCapacity = 32)
    private val _stateFlow = MutableStateFlow(ITransport.TransportState.DISCONNECTED)
    private val _errorFlow = MutableSharedFlow<ITransport.TransportError>(extraBufferCapacity = 8)

    @Volatile
    private var serverSocket: ServerSocket? = null

    @Volatile
    private var clientSocket: Socket? = null

    @Volatile
    private var currentState = ITransport.TransportState.DISCONNECTED

    @Volatile
    private var listeningPort = DEFAULT_PORT

    init {
        logger.info(tag, "USB transport initialized for ADB forwarding mode")
    }

    override suspend fun connect(target: String): Boolean {
        if (serverSocket != null) {
            logger.debug(tag, "USB listener already active on ${getServerEndpoint()}")
            return true
        }

        val port = parsePort(target)
        return try {
            listeningPort = port
            updateState(ITransport.TransportState.CONNECTING)

            serverSocket = ServerSocket(port).apply {
                reuseAddress = true
            }

            logger.info(tag, "USB listener started on ${getServerEndpoint()}")
            transportScope.launch { acceptLoop() }
            true
        } catch (e: Exception) {
            logger.error(tag, "Failed to start USB listener", e)
            emitError(ITransport.TransportError(1101, "Failed to start USB listener", e))
            updateState(ITransport.TransportState.ERROR)
            false
        }
    }

    override suspend fun disconnect() {
        try {
            logger.info(tag, "Stopping USB listener")
            updateState(ITransport.TransportState.DISCONNECTING)
            closeClientSocket()
            serverSocket?.close()
            serverSocket = null
            updateState(ITransport.TransportState.DISCONNECTED)
        } catch (e: Exception) {
            logger.error(tag, "USB disconnect error", e)
            emitError(ITransport.TransportError(1102, "USB disconnect error", e))
            updateState(ITransport.TransportState.ERROR)
        }
    }

    override suspend fun send(data: ByteArray): Boolean {
        val socket = clientSocket
        return try {
            if (socket == null || socket.isClosed || !socket.isConnected) {
                logger.warn(tag, "Send requested with no active USB client")
                return false
            }

            socket.getOutputStream().apply {
                write(data)
                write('\n'.code)
                flush()
            }

            logger.debug(tag, "Sent ${data.size} bytes via USB")
            true
        } catch (e: Exception) {
            logger.error(tag, "USB send error", e)
            emitError(ITransport.TransportError(1103, "USB send error", e))
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

    override fun getTransportType(): String = "USB / ADB"

    override suspend fun cleanup() {
        logger.info(tag, "Cleaning up USB transport")
        disconnect()
        transportScope.cancel()
    }

    fun getServerEndpoint(): String = "$LOCALHOST:$listeningPort"

    fun isListening(): Boolean = serverSocket?.let { !it.isClosed } == true

    fun getConnectedClientEndpoint(): String {
        val socket = clientSocket ?: return "لا يوجد جهاز متصل عبر USB"
        val host = socket.inetAddress?.hostAddress ?: "127.0.0.1"
        return "$host:${socket.port}"
    }

    private suspend fun acceptLoop() {
        while (serverSocket != null) {
            try {
                val acceptedSocket = serverSocket?.accept() ?: break
                closeClientSocket()
                clientSocket = acceptedSocket
                updateState(ITransport.TransportState.CONNECTED)
                logger.info(tag, "USB client connected: ${getConnectedClientEndpoint()}")
                readLoop(acceptedSocket)
            } catch (e: Exception) {
                if (serverSocket == null) {
                    break
                }

                logger.error(tag, "USB accept loop error", e)
                emitError(ITransport.TransportError(1104, "USB accept loop error", e))
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
                logger.debug(tag, "Received ${payload.size} bytes from USB client")
            }
        } catch (e: Exception) {
            if (serverSocket != null) {
                logger.error(tag, "USB receive loop error", e)
                emitError(ITransport.TransportError(1105, "USB receive loop error", e))
            }
        } finally {
            closeClientSocket()
            if (serverSocket != null) {
                updateState(ITransport.TransportState.CONNECTING)
                logger.info(tag, "USB client disconnected, listener still active on ${getServerEndpoint()}")
            }
        }
    }

    private fun closeClientSocket() {
        clientSocket?.runCatching { close() }
        clientSocket = null
    }

    private fun parsePort(target: String): Int {
        val trimmed = target.trim()
        if (trimmed.isBlank()) {
            return DEFAULT_PORT
        }

        val portToken = trimmed.substringAfter(':', trimmed)
        return portToken.toIntOrNull()?.takeIf { it in 1..65535 } ?: DEFAULT_PORT
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
        private const val LOCALHOST = "127.0.0.1"
    }
}
