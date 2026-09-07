package com.netrelay.connectivity.protocol

/**
 * Protocol message type definitions.
 * Each message type represents a specific communication intent.
 */
object MessageTypes {

    // Handshake and pairing
    const val HANDSHAKE = "HANDSHAKE"
    const val HANDSHAKE_RESPONSE = "HANDSHAKE_RESPONSE"
    const val PAIRING_REQUEST = "PAIRING_REQUEST"
    const val PAIRING_RESPONSE = "PAIRING_RESPONSE"
    const val PAIRING_CONFIRMED = "PAIRING_CONFIRMED"

    // Keep-alive
    const val HEARTBEAT = "HEARTBEAT"
    const val HEARTBEAT_ACK = "HEARTBEAT_ACK"

    // Data transfer
    const val DATA_TRANSFER = "DATA_TRANSFER"
    const val DATA_ACK = "DATA_ACK"

    // Status
    const val STATUS_UPDATE = "STATUS_UPDATE"
    const val STATUS_QUERY = "STATUS_QUERY"

    // Error handling
    const val ERROR = "ERROR"
    const val ERROR_ACK = "ERROR_ACK"

    // Connection management
    const val DISCONNECT = "DISCONNECT"
    const val DISCONNECT_ACK = "DISCONNECT_ACK"
    const val RECONNECT_REQUEST = "RECONNECT_REQUEST"

    // Protocol
    const val PROTOCOL_VERSION = "1.0"
}

/**
 * Protocol error codes.
 */
object ErrorCodes {
    const val UNKNOWN_ERROR = 0
    const val INVALID_MESSAGE = 1
    const val AUTHENTICATION_FAILED = 2
    const val INVALID_SESSION = 3
    const val TRANSPORT_ERROR = 4
    const val TIMEOUT = 5
    const val PROTOCOL_VERSION_MISMATCH = 6
    const val DEVICE_INCOMPATIBLE = 7
}
