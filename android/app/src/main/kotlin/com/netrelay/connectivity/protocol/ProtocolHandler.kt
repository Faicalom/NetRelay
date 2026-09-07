package com.netrelay.connectivity.protocol

import com.netrelay.connectivity.util.Logger
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow

/**
 * Handles incoming protocol messages and coordinates responses.
 * Routes messages to appropriate handlers and manages protocol flow.
 */
class ProtocolHandler(
    private val serializer: MessageSerializer
) {

    private val logger = Logger
    private val tag = "ProtocolHandler"

    private val _incomingMessages = MutableSharedFlow<ProtocolMessage>(replay = 0)
    val incomingMessages = _incomingMessages.asSharedFlow()

    private val _outgoingMessages = MutableSharedFlow<ProtocolMessage>(replay = 0)
    val outgoingMessages = _outgoingMessages.asSharedFlow()

    private val messageHandlers = mutableMapOf<String, suspend (ProtocolMessage) -> Unit>()

    /**
     * Register message handler for specific message type.
     */
    fun registerMessageHandler(
        messageType: String,
        handler: suspend (ProtocolMessage) -> Unit
    ) {
        messageHandlers[messageType] = handler
        logger.debug(tag, "Message handler registered for: $messageType")
    }

    /**
     * Process incoming raw data and parse as protocol message.
     */
    suspend fun processIncoming(data: ByteArray) {
        val message = serializer.deserialize(data)
        if (message != null) {
            logger.debug(tag, "Incoming message: ${message.type} from session: ${message.sessionId}")
            _incomingMessages.emit(message)

            // Route to specific handler if registered
            val handler = messageHandlers[message.type]
            if (handler != null) {
                try {
                    handler(message)
                } catch (e: Exception) {
                    logger.error(tag, "Error handling message type: ${message.type}", e)
                }
            }
        } else {
            logger.warn(tag, "Failed to deserialize incoming message")
        }
    }

    /**
     * Send outgoing protocol message.
     */
    suspend fun sendMessage(message: ProtocolMessage): ByteArray {
        logger.debug(tag, "Sending message: ${message.type} to session: ${message.sessionId}")
        _outgoingMessages.emit(message)
        return serializer.serialize(message)
    }

    /**
     * Send handshake message.
     */
    suspend fun sendHandshake(
        deviceName: String,
        deviceId: String,
        osVersion: String,
        appVersion: String,
        supportedTransports: List<String>,
        sessionId: String? = null
    ): ByteArray {
        val handshake = HandshakeMessage(
            deviceName = deviceName,
            deviceId = deviceId,
            osVersion = osVersion,
            appVersion = appVersion,
            supportedTransports = supportedTransports
        )
        val message = serializer.serializeHandshake(handshake, sessionId)
        return sendMessage(message)
    }

    /**
     * Send pairing request message.
     */
    suspend fun sendPairingRequest(
        deviceId: String,
        pairingToken: String,
        sessionTimeout: Long,
        sessionId: String? = null
    ): ByteArray {
        val pairing = PairingRequestMessage(
            deviceId = deviceId,
            pairingToken = pairingToken,
            sessionTimeout = sessionTimeout
        )
        val message = serializer.serializePairingRequest(pairing, sessionId)
        return sendMessage(message)
    }

    /**
     * Send heartbeat message.
     */
    suspend fun sendHeartbeat(
        deviceLoad: Float = 0f,
        sessionId: String? = null
    ): ByteArray {
        val heartbeat = HeartbeatMessage(deviceLoad = deviceLoad)
        val message = serializer.serializeHeartbeat(heartbeat, sessionId)
        return sendMessage(message)
    }

    /**
     * Send error message.
     */
    suspend fun sendError(
        errorCode: Int,
        errorMessage: String,
        details: String? = null,
        sessionId: String? = null
    ): ByteArray {
        val error = ErrorMessage(
            errorCode = errorCode,
            errorMessage = errorMessage,
            details = details
        )
        val message = serializer.serializeError(error, sessionId)
        return sendMessage(message)
    }

    /**
     * Send disconnect message.
     */
    suspend fun sendDisconnect(
        reason: String = "User initiated",
        willReconnect: Boolean = false,
        sessionId: String? = null
    ): ByteArray {
        val disconnect = DisconnectMessage(reason = reason, willReconnect = willReconnect)
        val message = serializer.serializeDisconnect(disconnect, sessionId)
        return sendMessage(message)
    }
}
