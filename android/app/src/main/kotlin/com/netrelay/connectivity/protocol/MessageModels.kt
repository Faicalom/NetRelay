package com.netrelay.connectivity.protocol

import kotlinx.serialization.Serializable
import java.util.UUID

/**
 * Base message structure for all protocol messages.
 * All messages follow this structure.
 */
@Serializable
data class ProtocolMessage(
    val messageId: String = UUID.randomUUID().toString(),
    val type: String,
    val timestamp: Long = System.currentTimeMillis(),
    val protocolVersion: String = MessageTypes.PROTOCOL_VERSION,
    val sessionId: String? = null,
    val payload: Map<String, String> = emptyMap()
)

/**
 * Handshake message - Initial connection greeting.
 */
@Serializable
data class HandshakeMessage(
    val deviceName: String,
    val deviceId: String,
    val osVersion: String,
    val appVersion: String,
    val supportedTransports: List<String> = emptyList()
)

/**
 * Pairing request - Request to establish session.
 */
@Serializable
data class PairingRequestMessage(
    val deviceId: String,
    val pairingToken: String,
    val sessionTimeout: Long = 3600000 // 1 hour in ms
)

/**
 * Pairing response - Acknowledgment with session token.
 */
@Serializable
data class PairingResponseMessage(
    val accepted: Boolean,
    val sessionToken: String? = null,
    val reasonIfRejected: String? = null
)

/**
 * Heartbeat message - Keep-alive ping.
 */
@Serializable
data class HeartbeatMessage(
    val timestamp: Long = System.currentTimeMillis(),
    val deviceLoad: Float = 0f
)

/**
 * Data transfer message - Payload data.
 */
@Serializable
data class DataTransferMessage(
    val sequenceNumber: Long,
    val dataSize: Int,
    val checksum: String,
    val isLast: Boolean = false
)

/**
 * Status update message - Device status information.
 */
@Serializable
data class StatusUpdateMessage(
    val status: String,
    val details: Map<String, String> = emptyMap()
)

/**
 * Error message - Error reporting.
 */
@Serializable
data class ErrorMessage(
    val errorCode: Int,
    val errorMessage: String,
    val details: String? = null
)

/**
 * Disconnect message - Connection termination.
 */
@Serializable
data class DisconnectMessage(
    val reason: String = "User initiated",
    val willReconnect: Boolean = false
)
