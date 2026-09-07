package com.netrelay.connectivity.service

import com.netrelay.connectivity.transport.ITransport

enum class TransportUiState {
    INACTIVE,
    ACTIVATING,
    ACTIVE,
    ERROR
}

/**
 * Converts real transport lifecycle facts into the small state model rendered by the UI.
 * Selection/preference is deliberately absent: selecting a transport is not proof that it is active.
 */
object TransportUiStatePolicy {
    const val USB = "usb"
    const val WIFI_DIRECT = "wifiDirect"

    fun fromRuntime(
        mode: String,
        lifecycle: ITransport.TransportState,
        listenerReady: Boolean,
        clientConnected: Boolean,
        wifiDirectGroupReady: Boolean
    ): TransportUiState {
        return when (lifecycle) {
            ITransport.TransportState.ERROR -> TransportUiState.ERROR
            ITransport.TransportState.DISCONNECTED,
            ITransport.TransportState.DISCONNECTING -> TransportUiState.INACTIVE

            ITransport.TransportState.CONNECTING -> when (mode) {
                WIFI_DIRECT -> if (wifiDirectGroupReady && listenerReady) {
                    TransportUiState.ACTIVE
                } else {
                    TransportUiState.ACTIVATING
                }
                USB -> TransportUiState.ACTIVATING
                else -> TransportUiState.INACTIVE
            }

            ITransport.TransportState.CONNECTED -> when (mode) {
                WIFI_DIRECT -> if (wifiDirectGroupReady && listenerReady && clientConnected) {
                    TransportUiState.ACTIVE
                } else {
                    TransportUiState.ERROR
                }
                USB -> if (listenerReady && clientConnected) {
                    TransportUiState.ACTIVE
                } else {
                    TransportUiState.ERROR
                }
                else -> TransportUiState.INACTIVE
            }
        }
    }

    fun shouldExposeWifiDirectInfo(state: TransportUiState): Boolean {
        return state == TransportUiState.ACTIVE
    }
}

/** Pure prerequisite decisions kept separate from Activity/UI code for unit testing. */
object TransportPrerequisitePolicy {
    fun mayStartUsb(adbEnabled: Boolean?): Boolean = adbEnabled != false

    fun needsLocationDialog(hasPermission: Boolean, locationEnabled: Boolean): Boolean {
        return hasPermission && !locationEnabled
    }
}
