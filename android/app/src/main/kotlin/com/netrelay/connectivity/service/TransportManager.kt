package com.netrelay.connectivity.service

import com.netrelay.connectivity.transport.ITransport
import com.netrelay.connectivity.util.Logger
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Manages available transport implementations.
 * Routes connection requests to appropriate transport.
 * CRITICAL: Each transport must be registered separately.
 */
class TransportManager {

    private val logger = Logger
    private val tag = "TransportManager"

    private val transports = mutableMapOf<String, ITransport>()
    private val _activeTransport = MutableStateFlow<ITransport?>(null)
    val activeTransport = _activeTransport.asStateFlow()

    /**
     * Register a transport implementation.
     * Each transport must be registered independently.
     */
    fun registerTransport(transportType: String, transport: ITransport) {
        transports[transportType] = transport
        logger.debug(tag, "Transport registered: $transportType")
    }

    /**
     * Get specific transport by type.
     */
    fun getTransport(transportType: String): ITransport? {
        return transports[transportType]
    }

    /**
     * Get all registered transport types.
     */
    fun getAvailableTransports(): List<String> {
        return transports.keys.toList()
    }

    /**
     * Set active transport for connections.
     */
    suspend fun setActiveTransport(transportType: String): Boolean {
        val transport = transports[transportType]
        return if (transport != null) {
            _activeTransport.emit(transport)
            logger.info(tag, "Active transport set to: $transportType")
            true
        } else {
            logger.error(tag, "Transport not found: $transportType")
            false
        }
    }

    /**
     * Connect using active transport.
     */
    suspend fun connect(target: String): Boolean {
        val transport = _activeTransport.value
        return if (transport != null) {
            logger.info(tag, "Connecting via ${transport.getTransportType()} to $target")
            transport.connect(target)
        } else {
            logger.error(tag, "No active transport selected")
            false
        }
    }

    /**
     * Disconnect from active transport.
     */
    suspend fun disconnect() {
        val transport = _activeTransport.value
        if (transport != null) {
            logger.info(tag, "Disconnecting from ${transport.getTransportType()}")
            transport.disconnect()
        }
    }

    /**
     * Send data through active transport.
     */
    suspend fun send(data: ByteArray): Boolean {
        val transport = _activeTransport.value
        return if (transport != null) {
            transport.send(data)
        } else {
            logger.error(tag, "No active transport for sending")
            false
        }
    }

    /**
     * Observe incoming data from active transport.
     */
    fun observeIncoming(): Flow<ByteArray>? {
        return _activeTransport.value?.observeIncoming()
    }

    /**
     * Observe state changes from active transport.
     */
    fun observeState(): Flow<ITransport.TransportState>? {
        return _activeTransport.value?.observeState()
    }

    /**
     * Get current connection state.
     */
    fun getState(): ITransport.TransportState? {
        return _activeTransport.value?.getState()
    }

    /**
     * Check if currently connected.
     */
    fun isConnected(): Boolean {
        return _activeTransport.value?.isConnected() == true
    }

    /**
     * Cleanup all transports.
     */
    suspend fun cleanup() {
        logger.info(tag, "Cleaning up all transports")
        transports.values.forEach { it.cleanup() }
        transports.clear()
        _activeTransport.emit(null)
    }
}
