package com.netrelay.connectivity.transport.wifi

import android.content.Context
import android.net.wifi.p2p.WifiP2pDevice
import android.net.wifi.p2p.WifiP2pManager
import com.netrelay.connectivity.util.Logger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.launch

/**
 * Handles Wi-Fi Direct device discovery.
 * Discovers available devices for connection.
 */
class WifiDirectDiscovery(
    private val manager: WifiP2pManager?,
    private val channel: WifiP2pManager.Channel?
) {

    private val logger = Logger
    private val tag = "WifiDirectDiscovery"

    private val _discoveredDevices = MutableSharedFlow<List<WifiP2pDevice>>()
    private val _discoveryStatus = MutableSharedFlow<DiscoveryStatus>()
    private val discoveryScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var snapshotJob: Job? = null

    enum class DiscoveryStatus {
        IDLE,
        DISCOVERING,
        DISCOVERY_STOPPED,
        ERROR
    }

    /**
     * Start discovering Wi-Fi Direct devices.
     */
    fun startDiscovery(): Boolean {
        return try {
            logger.info(tag, "Starting Wi-Fi Direct discovery")
            _discoveryStatus.tryEmit(DiscoveryStatus.DISCOVERING)
            manager?.discoverPeers(channel, object : WifiP2pManager.ActionListener {
                override fun onSuccess() {
                    logger.debug(tag, "Discovery started successfully")
                    startPeerSnapshots()
                }

                override fun onFailure(reasonCode: Int) {
                    logger.error(tag, "Discovery failed: $reasonCode")
                    _discoveryStatus.tryEmit(DiscoveryStatus.ERROR)
                }
            })
            true
        } catch (e: Exception) {
            logger.error(tag, "Start discovery error", e)
            false
        }
    }

    /**
     * Stop discovery process.
     */
    fun stopDiscovery(): Boolean {
        return try {
            logger.info(tag, "Stopping Wi-Fi Direct discovery")
            snapshotJob?.cancel()
            snapshotJob = null
            manager?.stopPeerDiscovery(channel, object : WifiP2pManager.ActionListener {
                override fun onSuccess() {
                    logger.debug(tag, "Discovery stopped successfully")
                    _discoveryStatus.tryEmit(DiscoveryStatus.DISCOVERY_STOPPED)
                }

                override fun onFailure(reasonCode: Int) {
                    logger.error(tag, "Stop discovery failed: $reasonCode")
                }
            })
            true
        } catch (e: Exception) {
            logger.error(tag, "Stop discovery error", e)
            false
        }
    }

    fun restartDiscovery(): Boolean {
        stopDiscovery()
        return startDiscovery()
    }

    /**
     * Request current peer list.
     */
    fun requestPeerList(): Boolean {
        return try {
            logger.debug(tag, "Requesting peer list")
            manager?.requestPeers(channel) { peers ->
                logger.debug(tag, "Discovered ${peers.deviceList.size} peers")
                try {
                    _discoveredDevices.tryEmit(peers.deviceList.toList())
                } catch (e: Exception) {
                    logger.error(tag, "Error emitting device list", e)
                }
            }
            true
        } catch (e: Exception) {
            logger.error(tag, "Request peer list error", e)
            false
        }
    }

    /**
     * Observe discovered devices.
     */
    fun observeDiscoveredDevices(): Flow<List<WifiP2pDevice>> = _discoveredDevices.asSharedFlow()

    /**
     * Observe discovery status changes.
     */
    fun observeDiscoveryStatus(): Flow<DiscoveryStatus> = _discoveryStatus.asSharedFlow()

    fun cleanup() {
        snapshotJob?.cancel()
        snapshotJob = null
        discoveryScope.cancel()
    }

    private fun startPeerSnapshots() {
        snapshotJob?.cancel()
        snapshotJob = discoveryScope.launch {
            repeat(8) { attempt ->
                delay(750L + (attempt * 250L))
                requestPeerList()
            }
            _discoveryStatus.tryEmit(DiscoveryStatus.IDLE)
        }
    }
}
