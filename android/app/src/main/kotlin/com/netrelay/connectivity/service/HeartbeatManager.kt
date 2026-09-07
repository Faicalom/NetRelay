package com.netrelay.connectivity.service

import com.netrelay.connectivity.protocol.ProtocolHandler
import com.netrelay.connectivity.util.Logger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/**
 * Manages connection heartbeat and keep-alive functionality.
 * Ensures connection remains alive with periodic pings.
 */
class HeartbeatManager(
    private val protocolHandler: ProtocolHandler,
    private val sessionManager: SessionManager
) {

    private val logger = Logger
    private val tag = "HeartbeatManager"
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    private val _isHeartbeatActive = MutableStateFlow(false)
    val isHeartbeatActive = _isHeartbeatActive.asStateFlow()

    private var heartbeatJob: Job? = null
    private var missedHeartbeats = 0
    private val maxMissedHeartbeats = 3
    private val heartbeatInterval = 30000L // 30 seconds
    private var lastHeartbeatTime = 0L

    /**
     * Start heartbeat monitoring.
     */
    suspend fun start() {
        if (_isHeartbeatActive.value) {
            logger.debug(tag, "Heartbeat already active")
            return
        }

        logger.info(tag, "Starting heartbeat monitor")
        _isHeartbeatActive.emit(true)
        missedHeartbeats = 0

        heartbeatJob = scope.launch {
            startHeartbeatLoop()
        }
    }

    /**
     * Stop heartbeat monitoring.
     */
    suspend fun stop() {
        logger.info(tag, "Stopping heartbeat monitor")
        heartbeatJob?.cancel()
        _isHeartbeatActive.emit(false)
        missedHeartbeats = 0
    }

    /**
     * Handle heartbeat acknowledgment from remote.
     */
    fun handleHeartbeatAck() {
        missedHeartbeats = 0
        lastHeartbeatTime = System.currentTimeMillis()
        logger.debug(tag, "Heartbeat acknowledged")
    }

    /**
     * Get connection health based on heartbeat status.
     */
    fun getConnectionHealth(): ConnectionHealth {
        return when {
            missedHeartbeats == 0 -> ConnectionHealth.EXCELLENT
            missedHeartbeats == 1 -> ConnectionHealth.GOOD
            missedHeartbeats == 2 -> ConnectionHealth.FAIR
            else -> ConnectionHealth.POOR
        }
    }

    enum class ConnectionHealth {
        EXCELLENT, GOOD, FAIR, POOR
    }

    private suspend fun startHeartbeatLoop() {
        while (_isHeartbeatActive.value) {
            try {
                delay(heartbeatInterval)

                if (sessionManager.isSessionActive()) {
                    sendHeartbeat()
                }
            } catch (e: Exception) {
                logger.error(tag, "Heartbeat loop error", e)
            }
        }
    }

    private suspend fun sendHeartbeat() {
        try {
            val session = sessionManager.getSession()
            if (session != null) {
                logger.debug(tag, "Sending heartbeat to session: ${session.sessionId}")
                protocolHandler.sendHeartbeat(sessionId = session.sessionId)
                missedHeartbeats++

                if (missedHeartbeats > maxMissedHeartbeats) {
                    logger.warn(tag, "Max missed heartbeats reached")
                    sessionManager.handleReconnect()
                }
            }
        } catch (e: Exception) {
            logger.error(tag, "Heartbeat send error", e)
            missedHeartbeats++
        }
    }
}
