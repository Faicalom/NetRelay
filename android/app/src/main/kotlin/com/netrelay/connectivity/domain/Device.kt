package com.netrelay.connectivity.domain

/**
 * Represents a connected device.
 */
data class Device(
    val deviceId: String,
    val deviceName: String,
    val osVersion: String,
    val appVersion: String,
    val lastSeen: Long = System.currentTimeMillis(),
    val isPaired: Boolean = false,
    val transportType: String = ""
)

/**
 * Connection session state.
 */
data class ConnectionSession(
    val sessionId: String,
    val device: Device,
    val sessionToken: String,
    val createdAt: Long = System.currentTimeMillis(),
    val sessionTimeout: Long = 3600000, // 1 hour
    val state: SessionState = SessionState.INITIATED
)

enum class SessionState {
    INITIATED,
    HANDSHAKING,
    PAIRING,
    ACTIVE,
    RECONNECTING,
    DISCONNECTING,
    CLOSED,
    ERROR
}

/**
 * Connection statistics.
 */
data class ConnectionStats(
    val dataBytesSent: Long = 0,
    val dataBytesReceived: Long = 0,
    val messagesReceived: Long = 0,
    val messagesSent: Long = 0,
    val packetsLost: Long = 0,
    val averageLatency: Long = 0,
    val connectionDuration: Long = 0
)
