package com.netrelay.connectivity.service

import com.netrelay.connectivity.transport.ITransport
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TransportUiStatePolicyTest {

    @After
    fun resetStore() {
        ConnectionRuntimeStore.reset()
    }

    @Test
    fun `clean startup and stale selection do not activate a transport`() {
        ConnectionRuntimeStore.updateSelectedMode(TransportUiStatePolicy.WIFI_DIRECT)

        assertEquals(TransportUiState.INACTIVE, ConnectionRuntimeStore.usbTransportState.value)
        assertEquals(TransportUiState.INACTIVE, ConnectionRuntimeStore.wifiDirectTransportState.value)
    }

    @Test
    fun `wifi direct stays activating until group and listener are real`() {
        val noGroup = TransportUiStatePolicy.fromRuntime(
            mode = TransportUiStatePolicy.WIFI_DIRECT,
            lifecycle = ITransport.TransportState.CONNECTING,
            listenerReady = false,
            clientConnected = false,
            wifiDirectGroupReady = false
        )
        val groupReady = TransportUiStatePolicy.fromRuntime(
            mode = TransportUiStatePolicy.WIFI_DIRECT,
            lifecycle = ITransport.TransportState.CONNECTING,
            listenerReady = true,
            clientConnected = false,
            wifiDirectGroupReady = true
        )

        assertEquals(TransportUiState.ACTIVATING, noGroup)
        assertEquals(TransportUiState.ACTIVE, groupReady)
    }

    @Test
    fun `removed wifi group becomes inactive and hides stale details`() {
        val state = TransportUiStatePolicy.fromRuntime(
            mode = TransportUiStatePolicy.WIFI_DIRECT,
            lifecycle = ITransport.TransportState.DISCONNECTED,
            listenerReady = false,
            clientConnected = false,
            wifiDirectGroupReady = false
        )

        assertEquals(TransportUiState.INACTIVE, state)
        assertFalse(TransportUiStatePolicy.shouldExposeWifiDirectInfo(state))
    }

    @Test
    fun `usb requires real listener and client before active`() {
        val starting = TransportUiStatePolicy.fromRuntime(
            mode = TransportUiStatePolicy.USB,
            lifecycle = ITransport.TransportState.CONNECTING,
            listenerReady = true,
            clientConnected = false,
            wifiDirectGroupReady = false
        )
        val connected = TransportUiStatePolicy.fromRuntime(
            mode = TransportUiStatePolicy.USB,
            lifecycle = ITransport.TransportState.CONNECTED,
            listenerReady = true,
            clientConnected = true,
            wifiDirectGroupReady = false
        )

        assertEquals(TransportUiState.ACTIVATING, starting)
        assertEquals(TransportUiState.ACTIVE, connected)
    }

    @Test
    fun `usb debugging off blocks activation while unknown safely falls back`() {
        assertFalse(TransportPrerequisitePolicy.mayStartUsb(false))
        assertTrue(TransportPrerequisitePolicy.mayStartUsb(true))
        assertTrue(TransportPrerequisitePolicy.mayStartUsb(null))
    }

    @Test
    fun `location dialog is independent from permission`() {
        assertTrue(TransportPrerequisitePolicy.needsLocationDialog(true, false))
        assertFalse(TransportPrerequisitePolicy.needsLocationDialog(true, true))
        assertFalse(TransportPrerequisitePolicy.needsLocationDialog(false, false))
    }

    @Test
    fun `only one runtime transport can remain active`() {
        ConnectionRuntimeStore.updateTransportUiState(TransportUiStatePolicy.USB, TransportUiState.ACTIVE)
        assertEquals(TransportUiState.ACTIVE, ConnectionRuntimeStore.usbTransportState.value)
        assertEquals(TransportUiState.INACTIVE, ConnectionRuntimeStore.wifiDirectTransportState.value)

        ConnectionRuntimeStore.updateTransportUiState(TransportUiStatePolicy.WIFI_DIRECT, TransportUiState.ACTIVE)
        assertEquals(TransportUiState.INACTIVE, ConnectionRuntimeStore.usbTransportState.value)
        assertEquals(TransportUiState.ACTIVE, ConnectionRuntimeStore.wifiDirectTransportState.value)
    }
}
