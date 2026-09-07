package com.netrelay.connectivity.protocol

import com.netrelay.connectivity.util.Logger
import kotlinx.serialization.encodeToString
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.json.Json
import java.nio.charset.StandardCharsets

/**
 * Serializes and deserializes protocol messages to/from JSON.
 * Handles all message types defined in the protocol.
 */
class MessageSerializer {

    private val logger = Logger
    private val tag = "MessageSerializer"

    private val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
    }

    /**
     * Serialize protocol message to JSON bytes.
     */
    fun serialize(message: ProtocolMessage): ByteArray {
        return try {
            val jsonString = json.encodeToString(message)
            jsonString.toByteArray(StandardCharsets.UTF_8)
        } catch (e: Exception) {
            logger.error(tag, "Failed to serialize message", e)
            ByteArray(0)
        }
    }

    /**
     * Deserialize JSON bytes to protocol message.
     */
    fun deserialize(data: ByteArray): ProtocolMessage? {
        return try {
            val jsonString = String(data, StandardCharsets.UTF_8)
            json.decodeFromString<ProtocolMessage>(jsonString)
        } catch (e: Exception) {
            logger.error(tag, "Failed to deserialize message", e)
            null
        }
    }

    /**
     * Serialize handshake message.
     */
    fun serializeHandshake(msg: HandshakeMessage, sessionId: String? = null): ProtocolMessage {
        return ProtocolMessage(
            type = MessageTypes.HANDSHAKE,
            sessionId = sessionId,
            payload = mapOf(
                "deviceName" to msg.deviceName,
                "deviceId" to msg.deviceId,
                "osVersion" to msg.osVersion,
                "appVersion" to msg.appVersion,
                "supportedTransports" to msg.supportedTransports.joinToString(",")
            )
        )
    }

    /**
     * Serialize pairing request.
     */
    fun serializePairingRequest(msg: PairingRequestMessage, sessionId: String? = null): ProtocolMessage {
        return ProtocolMessage(
            type = MessageTypes.PAIRING_REQUEST,
            sessionId = sessionId,
            payload = mapOf(
                "deviceId" to msg.deviceId,
                "pairingToken" to msg.pairingToken,
                "sessionTimeout" to msg.sessionTimeout.toString()
            )
        )
    }

    /**
     * Serialize heartbeat message.
     */
    fun serializeHeartbeat(msg: HeartbeatMessage, sessionId: String? = null): ProtocolMessage {
        return ProtocolMessage(
            type = MessageTypes.HEARTBEAT,
            sessionId = sessionId,
            payload = mapOf(
                "timestamp" to msg.timestamp.toString(),
                "deviceLoad" to msg.deviceLoad.toString()
            )
        )
    }

    /**
     * Serialize error message.
     */
    fun serializeError(msg: ErrorMessage, sessionId: String? = null): ProtocolMessage {
        return ProtocolMessage(
            type = MessageTypes.ERROR,
            sessionId = sessionId,
            payload = mapOf(
                "errorCode" to msg.errorCode.toString(),
                "errorMessage" to msg.errorMessage,
                "details" to (msg.details ?: "")
            )
        )
    }

    /**
     * Serialize disconnect message.
     */
    fun serializeDisconnect(msg: DisconnectMessage, sessionId: String? = null): ProtocolMessage {
        return ProtocolMessage(
            type = MessageTypes.DISCONNECT,
            sessionId = sessionId,
            payload = mapOf(
                "reason" to msg.reason,
                "willReconnect" to msg.willReconnect.toString()
            )
        )
    }
}
