package com.netrelay.connectivity.presentation

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.netrelay.connectivity.domain.Device
import com.netrelay.connectivity.protocol.ProtocolHandler
import com.netrelay.connectivity.protocol.MessageSerializer
import com.netrelay.connectivity.service.SessionManager
import com.netrelay.connectivity.service.TransportManager
import com.netrelay.connectivity.transport.ITransport
import com.netrelay.connectivity.util.Logger
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/**
 * Main view model for connection management.
 * Coordinates between UI, services, and transport layers.
 */
class ConnectionViewModel : ViewModel() {

    private val logger = Logger
    private val tag = "ConnectionViewModel"

    private val transportManager = TransportManager()
    private val sessionManager = SessionManager()
    private val serializer = MessageSerializer()
    private val protocolHandler = ProtocolHandler(serializer)

    private val _uiState = MutableStateFlow<UiState>(UiState.Idle)
    val uiState = _uiState.asStateFlow()

    private val _availableTransports = MutableStateFlow<List<String>>(emptyList())
    val availableTransports = _availableTransports.asStateFlow()

    private val _pairedDevices = MutableStateFlow<List<Device>>(emptyList())
    val pairedDevices = _pairedDevices.asStateFlow()

    init {
        logger.info(tag, "ConnectionViewModel initialized")
    }

    /**
     * Register transport implementation.
     */
    fun registerTransport(transportType: String, transport: ITransport) {
        transportManager.registerTransport(transportType, transport)
        _availableTransports.value = transportManager.getAvailableTransports()
        logger.debug(tag, "Transport registered: $transportType")
    }

    /**
     * Connect to device using selected transport.
     */
    fun connectToDevice(device: Device, transportType: String) {
        viewModelScope.launch {
            try {
                updateUiState(UiState.Connecting)

                val setActive = transportManager.setActiveTransport(transportType)
                if (!setActive) {
                    updateUiState(UiState.Error("Transport not available"))
                    return@launch
                }

                val sessionId = sessionManager.initiateSession(device)
                logger.info(tag, "Session initiated: $sessionId")

                val connected = transportManager.connect(device.deviceId)
                if (connected) {
                    sessionManager.startHandshake()
                    updateUiState(UiState.Connected(device))
                } else {
                    updateUiState(UiState.Error("Connection failed"))
                }
            } catch (e: Exception) {
                logger.error(tag, "Connection error", e)
                updateUiState(UiState.Error(e.message ?: "Unknown error"))
            }
        }
    }

    /**
     * Disconnect from device.
     */
    fun disconnect() {
        viewModelScope.launch {
            try {
                sessionManager.closeSession()
                transportManager.disconnect()
                updateUiState(UiState.Disconnected)
            } catch (e: Exception) {
                logger.error(tag, "Disconnect error", e)
            }
        }
    }

    /**
     * Send data to connected device.
     */
    fun sendData(data: ByteArray) {
        viewModelScope.launch {
            try {
                val sent = transportManager.send(data)
                if (!sent) {
                    logger.error(tag, "Failed to send data")
                }
            } catch (e: Exception) {
                logger.error(tag, "Send error", e)
            }
        }
    }

    private fun updateUiState(state: UiState) {
        viewModelScope.launch {
            _uiState.emit(state)
            logger.debug(tag, "UI State: $state")
        }
    }

    override fun onCleared() {
        viewModelScope.launch {
            transportManager.cleanup()
        }
        super.onCleared()
    }
}

sealed class UiState {
    object Idle : UiState()
    object Connecting : UiState()
    data class Connected(val device: Device) : UiState()
    object Disconnected : UiState()
    data class Error(val message: String) : UiState()
}
