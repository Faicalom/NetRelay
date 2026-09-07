package com.netrelay.connectivity.transport

import android.content.Context
import android.net.wifi.p2p.WifiP2pManager
import com.netrelay.connectivity.transport.usb.UsbTransport
import com.netrelay.connectivity.transport.wifi.WifiDirectTransport

/**
 * Creates platform-specific transport implementations for Android.
 * Keeps Android system-service wiring out of UI and service classes.
 */
object TransportFactory {

    fun createDefaultTransports(context: Context): Map<String, ITransport> {
        val appContext = context.applicationContext
        val transports = linkedMapOf<String, ITransport>()

        val wifiManager = appContext.getSystemService(Context.WIFI_P2P_SERVICE) as? WifiP2pManager
        val wifiChannel = wifiManager?.initialize(appContext, appContext.mainLooper, null)
        transports["wifiDirect"] = WifiDirectTransport(appContext, wifiManager, wifiChannel)

        transports["usb"] = UsbTransport()

        return transports
    }
}
