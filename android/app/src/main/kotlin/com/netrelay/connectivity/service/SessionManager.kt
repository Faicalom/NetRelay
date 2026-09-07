package com.netrelay.connectivity.service

import com.netrelay.connectivity.domain.ConnectionSession
import com.netrelay.connectivity.domain.Device
import com.netrelay.connectivity.domain.SessionState
import com.netrelay.connectivity.util.Logger
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.UUID

/**
 * Manages connection sessions.
 * Tracks pairing, session tokens, timeouts, and reconnections.
 */
class SessionManager {

    private val logger = Logger
    private val tag = "SessionManager"

    private val _currentSession = MutableStateFlow<ConnectionSession?>(null)
    val currentSession = _currentSession.asStateFlow()

    private val _sessionState = MutableStateFlow<SessionState>(SessionState.CLOSED)
    val sessionState = _sessionState.asStateFlow()

    private val pairedDevices = mutableMapOf<String, Device>()

    /**
     * Initiate new session with device.
     */
    suspend fun initiateSession(device: Device): String {
        val sessionId = UUID.randomUUID().toString()
        val session = ConnectionSession(
            sessionId = sessionId,
            device = device,
            sessionToken = generateSessionToken(),
            state = SessionState.INITIATED
        )

        _currentSession.emit(session)
        updateSessionState(SessionState.INITIATED)
        logger.info(tag, "Session initiated: $sessionId for device: ${device.deviceName}")

        return sessionId
    }

    /**
     * Move session to handshake phase.
     */
    suspend fun startHandshake(): Boolean {
        val session = _currentSession.value
        return if (session != null) {
            updateSessionState(SessionState.HANDSHAKING)
            logger.debug(tag, "Handshake started for session: ${session.sessionId}")
            true
        } else {
            logger.error(tag, "No active session for handshake")
            false
        }
    }

    /**
     * Move session to pairing phase.
     */
    suspend fun startPairing(): Boolean {
        val session = _currentSession.value
        return if (session != null) {
            updateSessionState(SessionState.PAIRING)
            logger.debug(tag, "Pairing started for session: ${session.sessionId}")
            true
        } else {
            logger.error(tag, "No active session for pairing")
            false
        }
    }

    /**
     * Mark session as active (fully connected).
     */
    suspend fun markActive(): Boolean {
        val session = _currentSession.value
        return if (session != null) {
            val device = session.device
            pairedDevices[device.deviceId] = device.copy(isPaired = true)
            updateSessionState(SessionState.ACTIVE)
            logger.info(tag, "Session marked as ACTIVE: ${session.sessionId}")
            true
        } else {
            logger.error(tag, "No active session to mark")
            false
        }
    }

    /**
     * Get current session.
     */
    fun getSession(): ConnectionSession? = _currentSession.value

    /**
     * Get current session state.
     */
    fun getSessionState(): SessionState = _sessionState.value

    /**
     * Check if session is active.
     */
    fun isSessionActive(): Boolean {
        return _sessionState.value == SessionState.ACTIVE
    }

    /**
     * Validate session token.
     */
    fun validateSessionToken(token: String): Boolean {
        val session = _currentSession.value
        return session?.sessionToken == token
    }

    /**
     * Get paired devices.
     */
    fun getPairedDevices(): List<Device> = pairedDevices.values.toList()

    /**
     * Check if device is paired.
     */
    fun isDevicePaired(deviceId: String): Boolean = pairedDevices.containsKey(deviceId)

    /**
     * Add paired device.
     */
    fun addPairedDevice(device: Device) {
        pairedDevices[device.deviceId] = device.copy(isPaired = true)
        logger.debug(tag, "Device paired: ${device.deviceName}")
    }

    /**
     * Remove paired device.
     */
    fun removePairedDevice(deviceId: String) {
        pairedDevices.remove(deviceId)
        logger.debug(tag, "Device unpaired: $deviceId")
    }

    /**
     * Handle reconnect scenario.
     */
    suspend fun handleReconnect() {
        updateSessionState(SessionState.RECONNECTING)
        logger.info(tag, "Session reconnecting...")
    }

    /**
     * Close session.
     */
    suspend fun closeSession(reason: String = "User initiated") {
        val session = _currentSession.value
        if (session != null) {
            logger.info(tag, "Session closed: ${session.sessionId} - Reason: $reason")
        }
        updateSessionState(SessionState.CLOSED)
        _currentSession.emit(null)
    }

    /**
     * Mark session as error state.
     */
    suspend fun markError(error: String) {
        updateSessionState(SessionState.ERROR)
        logger.error(tag, "Session error: $error")
    }

    private suspend fun updateSessionState(state: SessionState) {
        _sessionState.emit(state)
    }

    private fun generateSessionToken(): String {
        return UUID.randomUUID().toString().replace("-", "")
    }
}
