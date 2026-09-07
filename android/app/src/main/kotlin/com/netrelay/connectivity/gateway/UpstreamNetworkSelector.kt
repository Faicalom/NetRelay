package com.netrelay.connectivity.gateway

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import com.netrelay.connectivity.util.Logger
import java.net.InetAddress
import java.net.NoRouteToHostException
import java.net.UnknownHostException
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Chooses the Android egress network for AEL2 Internet flows.
 *
 * A VPN is deliberately never selected as a raw "best" candidate.  Android's
 * application default network is the authority for whether a VPN applies to this
 * app.  That prevents an excluded application from silently escaping through a
 * cellular or Wi-Fi network while the user expects VPN-only egress.
 */
class UpstreamNetworkSelector(context: Context) {
    private val logger = Logger
    private val tag = "UpstreamNetworkSelector"
    private val connectivityManager =
        context.applicationContext.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
    private val monitoring = AtomicBoolean(false)

    /** Once this gateway has used an applicable VPN, new flows fail closed if it disappears. */
    @Volatile
    private var vpnRequiredForSession = false

    data class EgressNetwork(
        val network: Network?,
        val isVpn: Boolean,
        val label: String
    )

    class VpnPolicyException(message: String) : NoRouteToHostException(message)

    private val defaultNetworkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = logNetworkSnapshot("Default network available=$network")

        override fun onCapabilitiesChanged(network: Network, capabilities: NetworkCapabilities) {
            logNetworkSnapshot("Default network capabilities changed=$network")
        }

        override fun onLost(network: Network) {
            logNetworkSnapshot("Default network lost=$network")
        }
    }

    fun startMonitoring() {
        if (!monitoring.compareAndSet(false, true)) return
        try {
            connectivityManager.registerDefaultNetworkCallback(defaultNetworkCallback)
            logNetworkSnapshot("Checking application default network")
        } catch (e: Exception) {
            monitoring.set(false)
            logger.warn(tag, "[NETRELAY-VPN] Could not register default network callback: ${e.message}", e)
            logNetworkSnapshot("Checking application default network without callback")
        }
    }

    fun close() {
        if (monitoring.compareAndSet(true, false)) {
            runCatching { connectivityManager.unregisterNetworkCallback(defaultNetworkCallback) }
        }
    }

    /**
     * Selects an egress for one new TCP/UDP/DNS flow. It intentionally queries
     * ConnectivityManager every time, so a stale VPN Network is never reused.
     */
    fun selectForNewFlow(): EgressNetwork {
        val active = connectivityManager.activeNetwork
        val activeCapabilities = active?.let { connectivityManager.getNetworkCapabilities(it) }
        val vpnExists = connectivityManager.allNetworks.any { network ->
            connectivityManager.getNetworkCapabilities(network)?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == true
        }
        val activeIsVpn = activeCapabilities?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == true
        val activeHasInternet = activeCapabilities?.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) == true

        if (activeIsVpn && activeHasInternet && active != null) {
            vpnRequiredForSession = true
            logger.info(tag, "[NETRELAY-VPN] VPN_APPLIES_TO_NETRELAY=true network=$active")
            return EgressNetwork(active, isVpn = true, label = "VPN")
        }

        if (vpnRequiredForSession) {
            val message = "VPN lost; blocking new Internet flows to avoid physical-network leak"
            logger.warn(tag, "[NETRELAY-VPN] $message")
            throw VpnPolicyException(message)
        }

        if (vpnExists) {
            val message = "VPN is active but does not apply to NetRelay. Check the VPN application's per-app / split-tunneling settings."
            logger.warn(tag, "[NETRELAY-VPN] $message")
            throw VpnPolicyException(message)
        }

        val selected = selectPhysicalInternetNetwork(active)
        logger.info(tag, "[NETRELAY-VPN] VPN_APPLIES_TO_NETRELAY=false egress=PHYSICAL_DEFAULT network=$selected")
        return EgressNetwork(selected, isVpn = false, label = "PHYSICAL_DEFAULT")
    }

    /** Resolves DNS through exactly the same selected network as the data flow. */
    fun resolve(egress: EgressNetwork, host: String): InetAddress {
        val value = host.trim()
        if (value.isBlank()) throw UnknownHostException("empty host")

        val numericCandidate = value.contains(':') || value.all { it.isDigit() || it == '.' }
        if (numericCandidate) return InetAddress.getByName(value)

        val network = egress.network
        if (network != null) {
            val addresses = network.getAllByName(value)
            if (addresses.isNotEmpty()) return addresses[0]
            throw UnknownHostException(value)
        }
        return InetAddress.getByName(value)
    }

    private fun selectPhysicalInternetNetwork(active: Network?): Network? {
        val candidates = connectivityManager.allNetworks.mapNotNull { network ->
            val capabilities = connectivityManager.getNetworkCapabilities(network) ?: return@mapNotNull null
            if (!capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) ||
                capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
            ) {
                return@mapNotNull null
            }
            Candidate(network, score(capabilities) + if (network == active) 200 else 0)
        }.sortedByDescending { it.score }

        val selected = candidates.firstOrNull()?.network
        if (selected == null) {
            logger.warn(tag, "[NETRELAY-VPN] No physical Internet network found; system default socket routing will be used")
        }
        return selected
    }

    private fun logNetworkSnapshot(reason: String) {
        val active = connectivityManager.activeNetwork
        val activeCapabilities = active?.let { connectivityManager.getNetworkCapabilities(it) }
        logger.info(tag, "[NETRELAY-VPN] $reason")
        logger.info(
            tag,
            "[NETRELAY-VPN] activeNetwork=$active transport VPN=${hasTransport(activeCapabilities, NetworkCapabilities.TRANSPORT_VPN)} " +
                "INTERNET=${hasCapability(activeCapabilities, NetworkCapabilities.NET_CAPABILITY_INTERNET)} " +
                "VALIDATED=${hasCapability(activeCapabilities, NetworkCapabilities.NET_CAPABILITY_VALIDATED)}"
        )
        connectivityManager.allNetworks.forEach { network ->
            val capabilities = connectivityManager.getNetworkCapabilities(network)
            logger.info(
                tag,
                "[NETRELAY-VPN] network=$network VPN=${hasTransport(capabilities, NetworkCapabilities.TRANSPORT_VPN)} " +
                    "CELL=${hasTransport(capabilities, NetworkCapabilities.TRANSPORT_CELLULAR)} " +
                    "WIFI=${hasTransport(capabilities, NetworkCapabilities.TRANSPORT_WIFI)} " +
                    "INTERNET=${hasCapability(capabilities, NetworkCapabilities.NET_CAPABILITY_INTERNET)} " +
                    "VALIDATED=${hasCapability(capabilities, NetworkCapabilities.NET_CAPABILITY_VALIDATED)} " +
                    "NOT_VPN=${hasCapability(capabilities, NetworkCapabilities.NET_CAPABILITY_NOT_VPN)}"
            )
        }
    }

    private fun hasTransport(capabilities: NetworkCapabilities?, transport: Int): Boolean =
        capabilities?.hasTransport(transport) == true

    private fun hasCapability(capabilities: NetworkCapabilities?, capability: Int): Boolean =
        capabilities?.hasCapability(capability) == true

    private fun score(capabilities: NetworkCapabilities): Int {
        var score = 0
        if (capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)) score += 1000
        if (capabilities.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_RESTRICTED)) score += 20
        score += when {
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> 30
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> 20
            capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> 15
            else -> 0
        }
        return score
    }

    private data class Candidate(val network: Network, val score: Int)
}
