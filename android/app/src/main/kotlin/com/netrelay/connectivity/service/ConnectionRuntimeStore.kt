package com.netrelay.connectivity.service

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Shared runtime state between the foreground service and the activity UI.
 * Keeps network endpoint details easy to display without binding to the service.
 */
object ConnectionRuntimeStore {

    private val _selectedMode = MutableStateFlow("wifiDirect")
    val selectedMode = _selectedMode.asStateFlow()

    private val _usbTransportState = MutableStateFlow(TransportUiState.INACTIVE)
    val usbTransportState = _usbTransportState.asStateFlow()

    private val _wifiDirectTransportState = MutableStateFlow(TransportUiState.INACTIVE)
    val wifiDirectTransportState = _wifiDirectTransportState.asStateFlow()

    private val _serviceState = MutableStateFlow("")
    val serviceState = _serviceState.asStateFlow()

    private val _wifiListenerEndpoint = MutableStateFlow("")
    val wifiListenerEndpoint = _wifiListenerEndpoint.asStateFlow()

    private val _wifiNetworkName = MutableStateFlow("")
    val wifiNetworkName = _wifiNetworkName.asStateFlow()

    private val _wifiPassphrase = MutableStateFlow("")
    val wifiPassphrase = _wifiPassphrase.asStateFlow()

    private val _wifiClientEndpoint = MutableStateFlow("")
    val wifiClientEndpoint = _wifiClientEndpoint.asStateFlow()

    private val _proxyEndpoint = MutableStateFlow("")
    val proxyEndpoint = _proxyEndpoint.asStateFlow()

    private val _proxyStatus = MutableStateFlow("")
    val proxyStatus = _proxyStatus.asStateFlow()

    private val _gatewayEndpoint = MutableStateFlow("")
    val gatewayEndpoint = _gatewayEndpoint.asStateFlow()

    private val _gatewayStatus = MutableStateFlow("")
    val gatewayStatus = _gatewayStatus.asStateFlow()

    private val _lastProtocolMessage = MutableStateFlow("")
    val lastProtocolMessage = _lastProtocolMessage.asStateFlow()

    fun updateSelectedMode(value: String) {
        _selectedMode.value = value
    }

    fun markTransportActivating(mode: String) {
        updateTransportUiState(mode, TransportUiState.ACTIVATING)
    }

    fun updateTransportUiState(mode: String, state: TransportUiState) {
        when (mode) {
            TransportUiStatePolicy.USB -> _usbTransportState.value = state
            TransportUiStatePolicy.WIFI_DIRECT -> _wifiDirectTransportState.value = state
        }

        if (state == TransportUiState.ACTIVE) {
            when (mode) {
                TransportUiStatePolicy.USB -> _wifiDirectTransportState.value = TransportUiState.INACTIVE
                TransportUiStatePolicy.WIFI_DIRECT -> _usbTransportState.value = TransportUiState.INACTIVE
            }
        }
    }

    fun transportUiState(mode: String): TransportUiState {
        return when (mode) {
            TransportUiStatePolicy.USB -> _usbTransportState.value
            TransportUiStatePolicy.WIFI_DIRECT -> _wifiDirectTransportState.value
            else -> TransportUiState.INACTIVE
        }
    }

    fun resetTransportUiStates() {
        _usbTransportState.value = TransportUiState.INACTIVE
        _wifiDirectTransportState.value = TransportUiState.INACTIVE
    }

    fun updateServiceState(value: String) {
        _serviceState.value = value
    }

    fun updateWifiListenerEndpoint(value: String) {
        _wifiListenerEndpoint.value = value
    }

    fun updateWifiNetworkName(value: String) {
        _wifiNetworkName.value = value
    }

    fun updateWifiPassphrase(value: String) {
        _wifiPassphrase.value = value
    }

    fun updateWifiClientEndpoint(value: String) {
        _wifiClientEndpoint.value = value
    }

    fun updateProxyEndpoint(value: String) {
        _proxyEndpoint.value = value
    }

    fun updateProxyStatus(value: String) {
        _proxyStatus.value = value
    }

    fun updateGatewayEndpoint(value: String) {
        _gatewayEndpoint.value = value
    }

    fun updateGatewayStatus(value: String) {
        _gatewayStatus.value = value
    }

    fun updateLastProtocolMessage(value: String) {
        _lastProtocolMessage.value = value
    }

    fun reset() {
        _selectedMode.value = "wifiDirect"
        resetTransportUiStates()
        _serviceState.value = ""
        _wifiListenerEndpoint.value = ""
        _wifiNetworkName.value = ""
        _wifiPassphrase.value = ""
        _wifiClientEndpoint.value = ""
        _proxyEndpoint.value = ""
        _proxyStatus.value = ""
        _gatewayEndpoint.value = ""
        _gatewayStatus.value = ""
        _lastProtocolMessage.value = ""
    }
}
