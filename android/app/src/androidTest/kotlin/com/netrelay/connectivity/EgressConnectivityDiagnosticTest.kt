package com.netrelay.connectivity

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.SystemClock
import android.util.Log
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Test
import org.junit.runner.RunWith
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket

@RunWith(AndroidJUnit4::class)
class EgressConnectivityDiagnosticTest {
    private val context: Context
        get() = InstrumentationRegistry.getInstrumentation().targetContext

    @Test
    fun compareBoundAndDefaultEgressOnce() {
        val connectivity = context.getSystemService(ConnectivityManager::class.java)
        val active = connectivity.activeNetwork
        val selected = active

        logNetworkSnapshot(connectivity, active, selected)
        requireNotNull(selected) { "No active Android network is available" }

        val destinations = listOf(
            "1.1.1.1" to 443,
            "1.0.0.1" to 443,
            "188.114.96.7" to 443
        )

        destinations.forEach { (host, port) ->
            tcpTest(
                label = "TCP_A",
                method = "Network.socketFactory",
                network = selected,
                host = host,
                port = port
            ) { selected.socketFactory.createSocket() }

            tcpTest(
                label = "TCP_B",
                method = "PLAIN_DEFAULT_SOCKET",
                network = null,
                host = host,
                port = port
            ) { Socket() }

            tcpTest(
                label = "TCP_C",
                method = "PLAIN_PLUS_NETWORK_BIND",
                network = selected,
                host = host,
                port = port
            ) {
                Socket().also { selected.bindSocket(it) }
            }
        }

        udpTest(
            label = "UDP_A",
            method = "NETWORK_BOUND",
            network = selected
        )
        udpTest(
            label = "UDP_B",
            method = "PLAIN_DEFAULT",
            network = null
        )
    }

    private fun logNetworkSnapshot(
        connectivity: ConnectivityManager,
        active: Network?,
        selected: Network?
    ) {
        val capabilities = selected?.let(connectivity::getNetworkCapabilities)
        val linkProperties = selected?.let(connectivity::getLinkProperties)
        val message = buildString {
            append("$PREFIX SNAPSHOT")
            append(" activeNetwork=$active")
            append(" selectedNetwork=$selected")
            append(" sameNetwork=${active == selected}")
            append(" cellular=${capabilities.has(NetworkCapabilities.TRANSPORT_CELLULAR)}")
            append(" wifi=${capabilities.has(NetworkCapabilities.TRANSPORT_WIFI)}")
            append(" vpn=${capabilities.has(NetworkCapabilities.TRANSPORT_VPN)}")
            append(" internet=${capabilities.cap(NetworkCapabilities.NET_CAPABILITY_INTERNET)}")
            append(" validated=${capabilities.cap(NetworkCapabilities.NET_CAPABILITY_VALIDATED)}")
            append(" notVpn=${capabilities.cap(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)}")
            append(" interface=${linkProperties?.interfaceName}")
            append(" addresses=${linkProperties?.linkAddresses}")
            append(" routes=${linkProperties?.routes}")
            append(" dns=${linkProperties?.dnsServers}")
            append(" capabilities=$capabilities")
            append(" linkProperties=$linkProperties")
        }
        Log.i(TAG, message)
    }

    private fun tcpTest(
        label: String,
        method: String,
        network: Network?,
        host: String,
        port: Int,
        socketFactory: () -> Socket
    ) {
        val started = SystemClock.elapsedRealtime()
        var socket: Socket? = null
        try {
            socket = socketFactory()
            socket.connect(InetSocketAddress(InetAddress.getByName(host), port), TIMEOUT_MS)
            val duration = SystemClock.elapsedRealtime() - started
            Log.i(
                TAG,
                "$PREFIX $label method=$method network=$network " +
                    "localAddress=${socket.localSocketAddress} destination=$host:$port " +
                    "result=SUCCESS exceptionClass= exceptionMessage= durationMs=$duration"
            )
        } catch (error: Throwable) {
            val duration = SystemClock.elapsedRealtime() - started
            Log.e(
                TAG,
                "$PREFIX $label method=$method network=$network " +
                    "localAddress=${socket?.localSocketAddress} destination=$host:$port " +
                    "result=FAIL exceptionClass=${error.javaClass.name} " +
                    "exceptionMessage=${error.message} durationMs=$duration",
                error
            )
        } finally {
            runCatching { socket?.close() }
        }
    }

    private fun udpTest(label: String, method: String, network: Network?) {
        val destination = InetSocketAddress(InetAddress.getByName(DNS_HOST), DNS_PORT)
        val query = dnsQuery()
        val started = SystemClock.elapsedRealtime()
        var socket: DatagramSocket? = null
        var sendResult = "NOT_ATTEMPTED"
        try {
            socket = if (network != null) {
                DatagramSocket(null).also {
                    network.bindSocket(it)
                    it.bind(InetSocketAddress(0))
                }
            } else {
                DatagramSocket()
            }
            socket.soTimeout = TIMEOUT_MS
            socket.send(DatagramPacket(query, query.size, destination))
            sendResult = "SUCCESS"

            val response = ByteArray(2048)
            val packet = DatagramPacket(response, response.size)
            socket.receive(packet)
            val transactionMatches = packet.length >= 2 &&
                response[0] == query[0] && response[1] == query[1]
            val duration = SystemClock.elapsedRealtime() - started
            Log.i(
                TAG,
                "$PREFIX $label method=$method network=$network " +
                    "localAddress=${socket.localSocketAddress} destination=$DNS_HOST:$DNS_PORT " +
                    "send=$sendResult receive=SUCCESS bytes=${packet.length} " +
                    "dnsTransactionMatch=$transactionMatches result=SUCCESS " +
                    "exceptionClass= exceptionMessage= durationMs=$duration"
            )
        } catch (error: Throwable) {
            val duration = SystemClock.elapsedRealtime() - started
            val receiveResult = if (error is java.net.SocketTimeoutException) "TIMEOUT" else "FAIL"
            Log.e(
                TAG,
                "$PREFIX $label method=$method network=$network " +
                    "localAddress=${socket?.localSocketAddress} destination=$DNS_HOST:$DNS_PORT " +
                    "send=$sendResult receive=$receiveResult result=FAIL " +
                    "exceptionClass=${error.javaClass.name} exceptionMessage=${error.message} " +
                    "durationMs=$duration",
                error
            )
        } finally {
            runCatching { socket?.close() }
        }
    }

    private fun dnsQuery(): ByteArray {
        val labels = "example.com".split('.')
        val output = ArrayList<Byte>()
        output += 0xA3.toByte()
        output += 0x7E.toByte()
        output += 0x01.toByte()
        output += 0x00.toByte()
        output += 0x00.toByte()
        output += 0x01.toByte()
        repeat(6) { output += 0x00.toByte() }
        labels.forEach { label ->
            val bytes = label.toByteArray(Charsets.US_ASCII)
            output += bytes.size.toByte()
            bytes.forEach { output += it }
        }
        output += 0x00.toByte()
        output += 0x00.toByte()
        output += 0x01.toByte()
        output += 0x00.toByte()
        output += 0x01.toByte()
        return output.toByteArray()
    }

    private fun NetworkCapabilities?.has(transport: Int): Boolean =
        this?.hasTransport(transport) == true

    private fun NetworkCapabilities?.cap(capability: Int): Boolean =
        this?.hasCapability(capability) == true

    private companion object {
        const val TAG = "NetRelayEgressTest"
        const val PREFIX = "[NETRELAY-EGRESS-TEST]"
        const val TIMEOUT_MS = 5_000
        const val DNS_HOST = "1.1.1.1"
        const val DNS_PORT = 53
    }
}
