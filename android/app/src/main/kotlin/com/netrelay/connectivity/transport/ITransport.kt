package com.netrelay.connectivity.transport

import kotlinx.coroutines.flow.Flow

/**
 * Abstract transport interface that all implementations must follow.
 * Each transport type (Wi-Fi Direct or USB) must implement this interface.
 *
 * CRITICAL RULE: Transport layer MUST be abstracted.
 * DO NOT mix transport implementations.
 */
interface ITransport {

    enum class TransportState {
        DISCONNECTED,
        CONNECTING,
        CONNECTED,
        DISCONNECTING,
        ERROR
    }

    data class TransportError(
        val code: Int,
        val message: String,
        val cause: Throwable? = null
    )

    /**
     * Establish connection with target device.
     * @param target Target device identifier (address, name, etc.)
     * @return true if connection successful
     */
    suspend fun connect(target: String): Boolean

    /**
     * Disconnect from target device.
     * Gracefully close all resources.
     */
    suspend fun disconnect()

    /**
     * Send data to connected device.
     * @param data Byte array to send
     * @return true if data sent successfully
     */
    suspend fun send(data: ByteArray): Boolean

    /**
     * Observe incoming data stream.
     * Emits received data packets.
     */
    fun observeIncoming(): Flow<ByteArray>

    /**
     * Observe transport state changes.
     * Emits state changes (CONNECTING, CONNECTED, DISCONNECTED, ERROR).
     */
    fun observeState(): Flow<TransportState>

    /**
     * Observe transport errors.
     * Emits errors that occur during operation.
     */
    fun observeErrors(): Flow<TransportError>

    /**
     * Get current transport state.
     */
    fun getState(): TransportState

    /**
     * Check if transport is currently connected.
     */
    fun isConnected(): Boolean

    /**
     * Get transport type name (WiFiDirect or USB).
     */
    fun getTransportType(): String

    /**
     * Cleanup resources.
     * Called when transport is no longer needed.
     */
    suspend fun cleanup()
}
