package com.netrelay.connectivity.service

import com.netrelay.connectivity.util.Logger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlin.math.pow

/**
 * Manages automatic reconnection with exponential backoff.
 * Handles reconnection attempts when connection is lost.
 */
class ReconnectionManager(
    private val transportManager: TransportManager,
    private val sessionManager: SessionManager
) {

    private val logger = Logger
    private val tag = "ReconnectionManager"
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    private val _isReconnecting = MutableStateFlow(false)
    val isReconnecting = _isReconnecting.asStateFlow()

    private val _reconnectAttempt = MutableStateFlow(0)
    val reconnectAttempt = _reconnectAttempt.asStateFlow()

    private var reconnectJob: Job? = null
    private val maxReconnectAttempts = 5
    private val initialBackoffMs = 1000L // 1 second

    /**
     * Start reconnection attempt.
     */
    suspend fun startReconnection(
        target: String,
        transportType: String
    ) {
        if (_isReconnecting.value) {
            logger.debug(tag, "Already reconnecting")
            return
        }

        logger.info(tag, "Starting reconnection sequence")
        _isReconnecting.emit(true)
        _reconnectAttempt.emit(0)

        reconnectJob = scope.launch {
            attemptReconnection(target, transportType)
        }
    }

    /**
     * Stop reconnection attempts.
     */
    suspend fun stopReconnection() {
        logger.info(tag, "Stopping reconnection")
        reconnectJob?.cancel()
        _isReconnecting.emit(false)
        _reconnectAttempt.emit(0)
    }

    /**
     * Get backoff time for current attempt.
     */
    private fun getBackoffTime(attempt: Int): Long {
        // Exponential backoff: 1s, 2s, 4s, 8s, 16s
        return initialBackoffMs * (2.0.pow(attempt - 1)).toLong()
    }

    private suspend fun attemptReconnection(
        target: String,
        transportType: String
    ) {
        for (attempt in 1..maxReconnectAttempts) {
            try {
                _reconnectAttempt.emit(attempt)
                val backoffTime = getBackoffTime(attempt)

                logger.info(tag, "Reconnection attempt $attempt/$maxReconnectAttempts (backoff: ${backoffTime}ms)")
                delay(backoffTime)

                val activeTransportSelected = transportManager.setActiveTransport(transportType)
                if (!activeTransportSelected) {
                    logger.error(tag, "Transport not available for reconnect: $transportType")
                    break
                }

                val connected = transportManager.connect(target)
                if (connected) {
                    logger.info(tag, "Reconnection successful on attempt $attempt")
                    sessionManager.markActive()
                    _isReconnecting.emit(false)
                    return
                }
            } catch (e: Exception) {
                logger.error(tag, "Reconnection attempt $attempt failed", e)
            }
        }

        logger.error(tag, "All reconnection attempts failed")
        sessionManager.markError("Failed to reconnect after $maxReconnectAttempts attempts")
        _isReconnecting.emit(false)
    }
}
