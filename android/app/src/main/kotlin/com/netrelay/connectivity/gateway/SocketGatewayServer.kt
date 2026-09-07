package com.netrelay.connectivity.gateway

import android.content.Context
import com.netrelay.connectivity.BuildConfig
import com.netrelay.connectivity.util.Logger
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExecutorCoroutineDispatcher
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.isActive
import kotlinx.coroutines.joinAll
import kotlinx.coroutines.launch
import kotlinx.coroutines.delay
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.ConnectException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NoRouteToHostException
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import java.util.concurrent.ThreadFactory
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import android.os.SystemClock

/**
 * Phone-side socket gateway inspired by the architecture observed in PdaNet:
 * Windows asks the phone to open real Internet sockets, and the phone returns data/events.
 *
 * NetRelay gateway implementation using the compatibility-preserved AEL2 framed protocol.
 */
class SocketGatewayServer(context: Context) {

    private val logger = Logger
    private val tag = "SocketGatewayServer"
    private val threadSequence = AtomicInteger(0)
    private val acceptDispatcher: ExecutorCoroutineDispatcher =
        Executors.newSingleThreadExecutor(namedThreadFactory("NetRelay-Accept"))
            .asCoroutineDispatcher()
    private val controlDispatcher: ExecutorCoroutineDispatcher =
        Executors.newFixedThreadPool(CONTROL_THREAD_COUNT, namedThreadFactory("NetRelay-Control"))
            .asCoroutineDispatcher()
    private val writerDispatcher: ExecutorCoroutineDispatcher =
        Executors.newSingleThreadExecutor(namedThreadFactory("NetRelay-Writer"))
            .asCoroutineDispatcher()
    private val connectDispatcher: ExecutorCoroutineDispatcher =
        Executors.newFixedThreadPool(TCP_CONNECT_MAX_CONCURRENCY, namedThreadFactory("NetRelay-Connect"))
            .asCoroutineDispatcher()
    private val udpControlDispatcher: ExecutorCoroutineDispatcher =
        Executors.newFixedThreadPool(UDP_CONTROL_THREAD_COUNT, namedThreadFactory("NetRelay-UdpControl"))
            .asCoroutineDispatcher()
    private val udpSendDispatcher: ExecutorCoroutineDispatcher =
        Executors.newSingleThreadExecutor(namedThreadFactory("NetRelay-UdpSend"))
            .asCoroutineDispatcher()
    private val scope = CoroutineScope(SupervisorJob() + acceptDispatcher)
    private val upstreamNetworkSelector = UpstreamNetworkSelector(context)

    @Volatile
    private var serverSocket: ServerSocket? = null

    @Volatile
    private var endpointHost: String? = null

    @Volatile
    private var activeClientSocket: Socket? = null

    private val activeSession = AtomicReference<GatewaySession?>(null)

    private val generationCounter = AtomicLong(0)
    private val lifecycleMutex = Mutex()

    suspend fun start(listenerEndpointHint: String? = null): Boolean = lifecycleMutex.withLock {
        upstreamNetworkSelector.startMonitoring()
        val requestedHost = extractHost(listenerEndpointHint) ?: "127.0.0.1"
        val existing = serverSocket
        if (existing != null && !existing.isClosed && endpointHost == requestedHost) {
            return@withLock true
        }

        if (existing != null && !existing.isClosed) {
            logger.info(tag, "Rebinding NetRelay AEL2 gateway from $endpointHost to $requestedHost")
            stopUnlocked(logStopped = false)
        }

        return@withLock try {
            val bindAddress = runCatching { InetAddress.getByName(requestedHost) }.getOrNull()
                ?: InetAddress.getByName("127.0.0.1")
            val listener = ServerSocket().apply {
                reuseAddress = true
                receiveBufferSize = SOCKET_BUFFER_SIZE
                bind(InetSocketAddress(bindAddress, DEFAULT_PORT), 32)
            }
            serverSocket = listener
            endpointHost = bindAddress.hostAddress ?: requestedHost
            logger.info(tag, "NetRelay AEL2 socket gateway started on ${getEndpoint()}")
            scope.launch { acceptLoop(listener) }
            true
        } catch (e: Exception) {
            logger.error(tag, "Failed to start socket gateway on $requestedHost:$DEFAULT_PORT", e)
            false
        }
    }

    suspend fun stop() = lifecycleMutex.withLock {
        stopUnlocked(logStopped = true)
    }

    private fun stopUnlocked(logStopped: Boolean) {
        val listener = serverSocket
        serverSocket = null
        listener?.runCatching { close() }

        activeSession.getAndSet(null)?.invalidate("gateway server stopped")
        val client = activeClientSocket
        activeClientSocket = null
        client?.runCatching { close() }

        endpointHost = null
        if (logStopped) {
            logger.info(tag, "NetRelay AEL2 socket gateway stopped")
        }
    }

    suspend fun cleanup() {
        stop()
        upstreamNetworkSelector.close()
        scope.cancel()
        udpSendDispatcher.close()
        udpControlDispatcher.close()
        connectDispatcher.close()
        writerDispatcher.close()
        controlDispatcher.close()
        acceptDispatcher.close()
    }

    fun isRunning(): Boolean = serverSocket?.let { !it.isClosed } == true

    fun isListeningOn(host: String): Boolean =
        isRunning() && endpointHost.equals(host, ignoreCase = true)

    fun getEndpoint(listenerEndpointHint: String? = null): String {
        val host = extractHost(listenerEndpointHint) ?: endpointHost ?: "0.0.0.0"
        return "$host:$DEFAULT_PORT"
    }

    private fun namedThreadFactory(prefix: String): ThreadFactory = ThreadFactory { runnable ->
        Thread(runnable, "$prefix-${threadSequence.incrementAndGet()}").apply {
            isDaemon = true
        }
    }

    private suspend fun acceptLoop(listener: ServerSocket) {
        while (scope.isActive && serverSocket === listener && !listener.isClosed) {
            try {
                val client = listener.accept()
                val generation = generationCounter.incrementAndGet()
                val session = GatewaySession(client, generation)
                val previousSession = activeSession.getAndSet(session)
                if (previousSession != null) {
                    logger.info(
                        tag,
                        "[NETRELAY-WRITER] SESSION_MUTEX_REPLACED " +
                            "oldGeneration=${previousSession.generation} newGeneration=$generation"
                    )
                }
                previousSession?.invalidate("replaced by generation $generation")
                val previous = activeClientSocket
                activeClientSocket = client
                if (previous != null && previous !== client && !previous.isClosed) {
                    logger.warn(tag, "Replacing stale gateway session with a newer connection")
                    previous.runCatching { close() }
                }

                scope.launch(controlDispatcher) {
                    try {
                        session.run()
                    } finally {
                        activeSession.compareAndSet(session, null)
                        if (activeClientSocket === client) {
                            activeClientSocket = null
                        }
                    }
                }
            } catch (e: Exception) {
                if (serverSocket !== listener || listener.isClosed) break
                logger.error(tag, "Gateway accept loop error", e)
            }
        }
    }

    private inner class GatewaySession(
        private val clientSocket: Socket,
        val generation: Long
    ) {
        private val tcpFlows = ConcurrentHashMap<Int, TcpFlow>()
        private val udpFlows = ConcurrentHashMap<Int, UdpFlow>()
        private val connectingSockets = ConcurrentHashMap<Int, Socket>()
        private val udpOpenTokens = ConcurrentHashMap<Int, Long>()
        private val udpTokenSequence = AtomicLong(0)
        private val sessionJob = SupervisorJob()
        private val controlScope = CoroutineScope(sessionJob + controlDispatcher)
        private val flowIoScope = CoroutineScope(sessionJob + Dispatchers.IO)
        private val udpControlScope = CoroutineScope(sessionJob + udpControlDispatcher)
        private val udpSendScope = CoroutineScope(sessionJob + udpSendDispatcher)
        private val udpSendQueue = Channel<GatewayProtocol.Frame>(UDP_SEND_PENDING_CAPACITY)
        private val udpReceiveJobs = AtomicInteger(0)
        private val udpSendActive = AtomicInteger(0)
        private val writerJob = SupervisorJob(sessionJob)
        private val writerScope = CoroutineScope(writerJob + writerDispatcher)
        private val sessionState = AtomicInteger(SESSION_ACTIVE)
        private var outboundWriter: GatewayOutboundWriter? = null
        private lateinit var input: DataInputStream
        private lateinit var output: DataOutputStream
        private val connectScheduler = BoundedConnectScheduler(
            sessionGeneration = generation,
            parentJob = sessionJob,
            metricsScope = controlScope,
            connectDispatcher = connectDispatcher,
            maxConcurrency = TCP_CONNECT_MAX_CONCURRENCY,
            pendingCapacity = TCP_CONNECT_PENDING_CAPACITY,
            nowElapsedMs = { SystemClock.elapsedRealtime() },
            isCurrentGeneration = { isCurrentGeneration() },
            execute = { request -> openTcp(request.value) },
            onSnapshot = { snapshot -> logConnectSnapshot(snapshot) }
        )
        private val udpOpenScheduler = BoundedUdpOpenScheduler(
            sessionGeneration = generation,
            parentJob = sessionJob,
            metricsScope = controlScope,
            udpControlDispatcher = udpControlDispatcher,
            maxConcurrency = UDP_OPEN_MAX_CONCURRENCY,
            pendingCapacity = UDP_OPEN_PENDING_CAPACITY,
            nowElapsedMs = { SystemClock.elapsedRealtime() },
            isCurrentGeneration = { isCurrentGeneration() },
            execute = { request -> openUdp(request) },
            onSnapshot = { snapshot -> logUdpSnapshot(snapshot) }
        )
        private val udpSendWorker = udpSendScope.launch { udpSendLoop() }

        init {
            logger.info(tag, "[NETRELAY-WRITER] SESSION_CREATED generation=$generation")
        }

        suspend fun run() {
            clientSocket.use { client ->
                try {
                    configureTcpSocket(client)
                    input = DataInputStream(client.getInputStream().buffered(BUFFER_SIZE))
                    output = DataOutputStream(client.getOutputStream().buffered(BUFFER_SIZE))
                    outboundWriter = createOutboundWriter()

                    logger.info(
                        tag,
                        "Gateway session #$generation connected from ${client.inetAddress?.hostAddress}:${client.port}"
                    )
                    val first = GatewayProtocol.readFrame(input)
                        ?: return
                    if (first.type != GatewayProtocol.Type.HELLO) {
                        throw ProtocolException("Gateway session must start with HELLO")
                    }
                    sendFrame(
                        GatewayProtocol.Frame(
                            type = GatewayProtocol.Type.HELLO_ACK,
                            arg = generation.toInt(),
                            payload = "NetRelay-Android-v2".toByteArray()
                        )
                    )
                    while (controlScope.isActive && !client.isClosed) {
                        val frame = GatewayProtocol.readFrame(input) ?: break
                        if (!isCurrentGeneration()) {
                            logger.debug(tag, "[NETRELAY-FRAME] stale frame dropped generation=$generation")
                            continue
                        }
                        handleFrame(frame)
                    }
                } catch (_: CancellationException) {
                } catch (e: Exception) {
                    logger.warn(tag, "Gateway session #$generation ended: ${e.message}", e)
                } finally {
                    invalidate("session run finished")
                    sessionState.set(SESSION_CLOSED)
                    logger.info(tag, "Gateway session #$generation closed")
                }
            }
        }

        private suspend fun handleFrame(frame: GatewayProtocol.Frame) {
            when (frame.type) {
                GatewayProtocol.Type.TCP_CONNECT -> submitTcpConnect(frame)
                GatewayProtocol.Type.TCP_DATA -> enqueueTcpData(frame)
                GatewayProtocol.Type.TCP_CLOSE -> enqueueTcpClose(frame.flowId)
                GatewayProtocol.Type.UDP_OPEN -> submitUdpOpen(frame.flowId, SystemClock.elapsedRealtime())
                GatewayProtocol.Type.UDP_DATAGRAM -> enqueueUdpSend(frame)
                GatewayProtocol.Type.UDP_CLOSE -> requestUdpClose(frame.flowId)
                GatewayProtocol.Type.PING -> {
                    val pong = GatewayProtocol.Frame(
                        type = GatewayProtocol.Type.PONG,
                        flowId = frame.flowId,
                        arg = frame.arg,
                        payload = frame.payload
                    )
                    sendFrame(pong)
                }
                else -> sendError(frame.flowId, GatewayProtocol.Status.COMMAND_NOT_SUPPORTED, "Unsupported frame ${frame.type}")
            }
        }

        private fun isCurrentGeneration(): Boolean =
            generation == generationCounter.get() &&
                activeSession.get() === this &&
                sessionState.get() == SESSION_ACTIVE

        fun invalidate(reason: String) {
            if (!sessionState.compareAndSet(SESSION_ACTIVE, SESSION_CLOSING)) return
            connectScheduler.invalidate()
            udpOpenScheduler.invalidate()
            udpOpenTokens.clear()
            udpSendQueue.close()
            connectingSockets.values.forEach { socket -> socket.runCatching { close() } }
            connectingSockets.clear()
            outboundWriter?.invalidate()
            outboundWriter = null
            writerScope.cancel()
            logger.info(tag, "[NETRELAY-OUTQ] SESSION_INVALIDATED generation=$generation reason=$reason")
            sessionJob.cancel()
            clientSocket.runCatching { close() }
            closeAllFlowsImmediately()
        }

        private fun closeAllFlowsImmediately() {
            tcpFlows.values.forEach { flow ->
                flow.writeQueue.close()
                flow.socket.runCatching { close() }
                flow.readJob?.cancel()
                flow.writeJob?.cancel()
            }
            tcpFlows.clear()
            udpFlows.values.forEach { flow ->
                flow.socket.runCatching { close() }
                flow.readJob?.cancel()
            }
            udpFlows.clear()
        }

        private suspend fun submitUdpOpen(flowId: Int, frameReceivedAtMs: Long) {
            val token = udpTokenSequence.incrementAndGet()
            udpOpenTokens[flowId] = token
            when (udpOpenScheduler.trySubmit(flowId, UdpOpenRequest(token), frameReceivedAtMs)) {
                BoundedUdpOpenScheduler.Admission.SUBMITTED -> Unit
                BoundedUdpOpenScheduler.Admission.STALE -> udpOpenTokens.remove(flowId, token)
                BoundedUdpOpenScheduler.Admission.REJECTED -> {
                    udpOpenTokens.remove(flowId, token)
                    logger.warn(
                        tag,
                        "[NETRELAY-UDP] admission rejected generation=$generation flow=$flowId " +
                            "pendingLimit=$UDP_OPEN_PENDING_CAPACITY"
                    )
                    sendFrame(
                        GatewayProtocol.Frame(
                            GatewayProtocol.Type.UDP_OPEN_RESULT,
                            flowId,
                            GatewayProtocol.Status.GENERAL_FAILURE,
                            payload = "UDP open admission queue full".toByteArray()
                        )
                    )
                }
            }
        }

        private fun requestUdpClose(flowId: Int) {
            udpOpenTokens.remove(flowId)
            udpControlScope.launch { closeUdpInternal(flowId, notify = false) }
        }

        private suspend fun enqueueUdpSend(frame: GatewayProtocol.Frame) {
            val owned = frame.ownedCopy()
            if (udpSendQueue.trySend(owned).isSuccess) return
            logger.warn(tag, "[NETRELAY-UDP] send queue full generation=$generation flow=${frame.flowId}")
            sendError(frame.flowId, GatewayProtocol.Status.GENERAL_FAILURE, "UDP send queue full")
        }

        private suspend fun udpSendLoop() {
            for (frame in udpSendQueue) {
                if (!isCurrentGeneration()) continue
                udpSendActive.incrementAndGet()
                try {
                    sendUdp(frame)
                } finally {
                    udpSendActive.updateAndGet { (it - 1).coerceAtLeast(0) }
                }
            }
        }

        private fun logUdpSnapshot(snapshot: BoundedUdpOpenScheduler.Snapshot) {
            logger.info(
                tag,
                "[NETRELAY-UDP] generation=${snapshot.sessionGeneration} activeUdpFlows=${udpFlows.size} " +
                    "activeUdpOpens=${snapshot.activeUdpOpens} pendingUdpOpens=${snapshot.pendingUdpOpens} " +
                    "udpOpenSucceeded=${snapshot.udpOpenSucceeded} udpOpenFailed=${snapshot.udpOpenFailed} " +
                    "udpOpenRejected=${snapshot.udpOpenRejected} " +
                    "maxFrameToOpenStartMs=${snapshot.maxFrameToOpenStartMs} " +
                    "maxOpenExecutionMs=${snapshot.maxOpenExecutionMs} " +
                    "maxFrameToResultMs=${snapshot.maxFrameToResultMs} " +
                    "udpReceiveJobs=${udpReceiveJobs.get()} udpSendActive=${udpSendActive.get()}"
            )
        }

        private fun createOutboundWriter(): GatewayOutboundWriter = GatewayOutboundWriter(
            sessionGeneration = generation,
            scope = writerScope,
            isCurrentGeneration = { isCurrentGeneration() },
            nowElapsedMs = { SystemClock.elapsedRealtime() },
            writeEncoded = { queued -> writeOutboundFrame(queued) },
            onSnapshot = { snapshot ->
                logger.info(
                    tag,
                    "[NETRELAY-OUTQ] generation=${snapshot.generation} " +
                        "controlDepth=${snapshot.controlDepth} flowControlDepth=${snapshot.flowControlDepth} " +
                        "dataDepth=${snapshot.dataDepth} controlOldestAgeMs=${snapshot.controlOldestAgeMs} " +
                        "flowControlOldestAgeMs=${snapshot.flowControlOldestAgeMs} " +
                        "dataOldestAgeMs=${snapshot.dataOldestAgeMs} enqueuedFrames=${snapshot.enqueuedFrames} " +
                        "dequeuedFrames=${snapshot.dequeuedFrames} tcpBackpressureCount=${snapshot.tcpBackpressureCount} " +
                        "udpDroppedCount=${snapshot.udpDroppedCount} writerRunning=${snapshot.writerRunning} " +
                        "writerLastProgressAgeMs=${snapshot.writerLastProgressAgeMs}"
                )
            },
            onSlowQueue = { event ->
                logger.warn(
                    tag,
                    "[NETRELAY-OUTQ] SLOW generation=${event.generation} priority=${event.priority} " +
                        "opcode=${event.type} flow=${event.flowId} ageMs=${event.ageMs}"
                )
            },
            onWriteFailure = { failure ->
                logger.warn(tag, "[NETRELAY-OUTQ] writer failed generation=$generation: ${failure.message}", failure)
                clientSocket.runCatching { close() }
                invalidate("outbound writer transport failure")
            }
        )

        private fun writeOutboundFrame(queued: GatewayOutboundWriter.QueuedFrame) {
            val writeStarted = SystemClock.elapsedRealtime()
            output.write(queued.encoded)
            val writeMs = SystemClock.elapsedRealtime() - writeStarted
            val flushStarted = SystemClock.elapsedRealtime()
            output.flush()
            val flushMs = SystemClock.elapsedRealtime() - flushStarted
            val totalMs = writeMs + flushMs
            if (totalMs > 500) {
                logger.warn(
                    tag,
                    "Slow gateway write opcode=${queued.type} frameLen=${queued.encoded.size} " +
                        "writeMs=$writeMs flushMs=$flushMs"
                )
            }
        }

        private suspend fun submitTcpConnect(frame: GatewayProtocol.Frame) {
            val owned = frame.ownedCopy()
            if (connectScheduler.trySubmit(owned.flowId, owned)) return
            if (!isCurrentGeneration()) return

            logger.warn(
                tag,
                "[NETRELAY-CONNECT] admission rejected generation=$generation flow=${owned.flowId} " +
                    "pendingLimit=$TCP_CONNECT_PENDING_CAPACITY"
            )
            sendFrame(
                GatewayProtocol.Frame(
                    GatewayProtocol.Type.TCP_CONNECT_RESULT,
                    owned.flowId,
                    GatewayProtocol.Status.GENERAL_FAILURE,
                    payload = "TCP connect admission queue full".toByteArray()
                )
            )
        }

        private fun logConnectSnapshot(snapshot: BoundedConnectScheduler.Snapshot) {
            val writer = outboundWriter?.snapshot()
            logger.info(
                tag,
                "[NETRELAY-CONNECT] generation=${snapshot.sessionGeneration} " +
                    "activeTcpConnects=${snapshot.activeTcpConnects} " +
                    "pendingTcpConnects=${snapshot.pendingTcpConnects} " +
                    "maxConcurrentTcpConnects=${snapshot.maxConcurrentTcpConnects} " +
                    "connectPermitsAvailable=${snapshot.connectPermitsAvailable} " +
                    "connectSucceeded=${snapshot.connectSucceeded} " +
                    "connectTimedOut=${snapshot.connectTimedOut} " +
                    "connectFailed=${snapshot.connectFailed} " +
                    "connectRejected=${snapshot.connectRejected} " +
                    "connectOldestPendingAgeMs=${snapshot.connectOldestPendingAgeMs} " +
                    "writerLastProgressAgeMs=${writer?.writerLastProgressAgeMs ?: -1} " +
                    "writerQueueControl=${writer?.controlDepth ?: -1} " +
                    "writerQueueFlowControl=${writer?.flowControlDepth ?: -1} " +
                    "writerQueueData=${writer?.dataDepth ?: -1}"
            )
        }

        private suspend fun openTcp(frame: GatewayProtocol.Frame): BoundedConnectScheduler.Outcome {
            if (!isCurrentGeneration()) return BoundedConnectScheduler.Outcome.STALE
            closeTcp(frame.flowId, notify = false)
            val host = GatewayProtocol.decodeHost(frame.payload).trim()
            val port = frame.arg
            if (host.isBlank() || port !in 1..65535) {
                sendFrame(
                    GatewayProtocol.Frame(
                        GatewayProtocol.Type.TCP_CONNECT_RESULT,
                        frame.flowId,
                        GatewayProtocol.Status.ADDRESS_NOT_SUPPORTED
                    )
                )
                return BoundedConnectScheduler.Outcome.FAILED
            }

            val egress = try {
                upstreamNetworkSelector.selectForNewFlow()
            } catch (e: UpstreamNetworkSelector.VpnPolicyException) {
                logger.warn(tag, "[NETRELAY-VPN] TCP flowId=${frame.flowId} blocked: ${e.message}")
                sendFrame(
                    GatewayProtocol.Frame(
                        GatewayProtocol.Type.TCP_CONNECT_RESULT,
                        frame.flowId,
                        GatewayProtocol.Status.NETWORK_UNREACHABLE,
                        payload = (e.message ?: "VPN policy blocked flow").toByteArray()
                    )
                )
                return BoundedConnectScheduler.Outcome.FAILED
            }
            logger.info(tag, "[NETRELAY-VPN] TCP flowId=${frame.flowId} egress=${egress.label}")
            val socket = egress.network?.socketFactory?.createSocket() ?: Socket()
            connectingSockets[frame.flowId] = socket
            try {
                if (!isCurrentGeneration()) {
                    socket.runCatching { close() }
                    return BoundedConnectScheduler.Outcome.STALE
                }
                configureTcpSocket(socket)
                logger.info(tag, "[NETRELAY-VPN] DNS TCP flowId=${frame.flowId} egress=${egress.label} host=$host")
                val address = upstreamNetworkSelector.resolve(egress, host)
                socket.connect(InetSocketAddress(address, port), CONNECT_TIMEOUT_MS)
                connectingSockets.remove(frame.flowId, socket)
                if (!isCurrentGeneration()) {
                    socket.runCatching { close() }
                    return BoundedConnectScheduler.Outcome.STALE
                }
                val flow = TcpFlow(socket)
                tcpFlows[frame.flowId] = flow
                flow.writeJob = flowIoScope.launch { writeTcpLoop(frame.flowId, flow) }

                val resultSent = sendFrame(
                    GatewayProtocol.Frame(
                        GatewayProtocol.Type.TCP_CONNECT_RESULT,
                        frame.flowId,
                        GatewayProtocol.Status.OK
                    )
                )
                if (!resultSent || !isCurrentGeneration()) {
                    closeTcp(frame.flowId, notify = false)
                    return BoundedConnectScheduler.Outcome.STALE
                }

                flow.readJob = flowIoScope.launch { readTcp(frame.flowId, flow) }
                return BoundedConnectScheduler.Outcome.SUCCEEDED
            } catch (e: Exception) {
                connectingSockets.remove(frame.flowId, socket)
                socket.runCatching { close() }
                if (!isCurrentGeneration()) return BoundedConnectScheduler.Outcome.STALE
                val status = statusForException(e)
                logger.debug(tag, "TCP connect failed $host:$port flow=${frame.flowId}: ${e.message}")
                sendFrame(
                    GatewayProtocol.Frame(
                        GatewayProtocol.Type.TCP_CONNECT_RESULT,
                        frame.flowId,
                        status,
                        payload = (e.message ?: "connect failed").toByteArray()
                    )
                )
                return if (e is SocketTimeoutException) {
                    BoundedConnectScheduler.Outcome.TIMED_OUT
                } else {
                    BoundedConnectScheduler.Outcome.FAILED
                }
            } finally {
                connectingSockets.remove(frame.flowId, socket)
            }
        }

        private suspend fun enqueueTcpData(frame: GatewayProtocol.Frame) {
            val flow = tcpFlows[frame.flowId] ?: run {
                sendError(frame.flowId, GatewayProtocol.Status.GENERAL_FAILURE, "Unknown TCP flow")
                return
            }
            if (flow.writeQueue.trySend(TcpWriteCommand(frame.payload.copyOf(), close = false)).isFailure) {
                logger.debug(tag, "TCP write queue closed flow=${frame.flowId}")
                closeTcp(frame.flowId, notify = true)
            }
        }

        private suspend fun enqueueTcpClose(flowId: Int) {
            val flow = tcpFlows[flowId] ?: return
            if (flow.writeQueue.trySend(TcpWriteCommand(ByteArray(0), close = true)).isFailure) {
                closeTcp(flowId, notify = false)
            }
        }

        private suspend fun writeTcpLoop(flowId: Int, flow: TcpFlow) {
            try {
                val outputStream = flow.socket.getOutputStream()
                for (command in flow.writeQueue) {
                    if (command.close) {
                        closeTcp(flowId, notify = false, cancelWriter = false)
                        return
                    }
                    outputStream.write(command.payload)
                    outputStream.flush()
                }
            } catch (e: Exception) {
                if (!flow.socket.isClosed) {
                    logger.debug(tag, "TCP write failed flow=$flowId: ${e.message}")
                }
                closeTcp(flowId, notify = true, cancelWriter = false)
            }
        }

        private suspend fun readTcp(flowId: Int, flow: TcpFlow) {
            try {
                val stream = flow.socket.getInputStream()
                val buffer = ByteArray(BUFFER_SIZE)
                while (flowIoScope.isActive && !flow.socket.isClosed) {
                    val count = stream.read(buffer)
                    if (count <= 0) break
                    sendFrame(
                        GatewayProtocol.Frame(
                            GatewayProtocol.Type.TCP_DATA_IN,
                            flowId,
                            payload = buffer.copyOf(count)
                        )
                    )
                }
            } catch (e: SocketTimeoutException) {
                logger.debug(tag, "TCP idle timeout flow=$flowId")
            } catch (e: Exception) {
                if (!flow.socket.isClosed) {
                    logger.debug(tag, "TCP read ended flow=$flowId: ${e.message}")
                }
            } finally {
                closeTcp(flowId, notify = true)
            }
        }

        private suspend fun openUdp(
            request: BoundedUdpOpenScheduler.Request<UdpOpenRequest>
        ): BoundedUdpOpenScheduler.Completion {
            val flowId = request.flowId
            val token = request.value.token
            val openStartedAt = SystemClock.elapsedRealtime()
            var socketCreatedAt = -1L
            var networkBoundAt = -1L
            closeUdpInternal(flowId, notify = false)
            var socket: DatagramSocket? = null
            try {
                if (!isUdpOpenCurrent(flowId, token)) {
                    return udpOpenCompletion(BoundedUdpOpenScheduler.Outcome.STALE, openStartedAt)
                }
                val egress = upstreamNetworkSelector.selectForNewFlow()
                logger.info(tag, "[NETRELAY-VPN] UDP flowId=$flowId egress=${egress.label}")
                val createdSocket = DatagramSocket(null)
                socket = createdSocket
                socketCreatedAt = SystemClock.elapsedRealtime()
                createdSocket.apply {
                    reuseAddress = true
                    receiveBufferSize = SOCKET_BUFFER_SIZE
                    sendBufferSize = SOCKET_BUFFER_SIZE
                    soTimeout = UDP_RECEIVE_TIMEOUT_MS
                    bind(InetSocketAddress(0))
                }
                // Binding is per socket. A failure is not ignored: silently falling back
                // to a physical network would leak traffic outside an applicable VPN.
                egress.network?.bindSocket(createdSocket)
                networkBoundAt = SystemClock.elapsedRealtime()
                if (!isUdpOpenCurrent(flowId, token)) {
                    createdSocket.close()
                    return udpOpenCompletion(BoundedUdpOpenScheduler.Outcome.STALE, openStartedAt)
                }
                val flow = UdpFlow(createdSocket, egress)
                udpFlows.put(flowId, flow)?.let { replaced ->
                    replaced.socket.runCatching { close() }
                    replaced.readJob?.cancel()
                }
                val resultSent = sendFrame(
                    GatewayProtocol.Frame(
                        GatewayProtocol.Type.UDP_OPEN_RESULT,
                        flowId,
                        GatewayProtocol.Status.OK,
                        payload = byteArrayOf()
                    )
                )
                val resultEnqueuedAt = SystemClock.elapsedRealtime()
                if (!resultSent || !isUdpOpenCurrent(flowId, token)) {
                    udpFlows.remove(flowId, flow)
                    createdSocket.close()
                    return udpOpenCompletion(BoundedUdpOpenScheduler.Outcome.STALE, openStartedAt)
                }
                flow.readJob = flowIoScope.launch {
                    udpReceiveJobs.incrementAndGet()
                    try {
                        readUdp(flowId, flow)
                    } finally {
                        udpReceiveJobs.updateAndGet { (it - 1).coerceAtLeast(0) }
                    }
                }
                logUdpOpenTrace(
                    request,
                    openStartedAt,
                    socketCreatedAt,
                    networkBoundAt,
                    resultEnqueuedAt
                )
                return BoundedUdpOpenScheduler.Completion(
                    BoundedUdpOpenScheduler.Outcome.SUCCEEDED,
                    openStartedAt,
                    resultEnqueuedAt
                )
            } catch (e: Exception) {
                socket?.runCatching { close() }
                udpFlows.remove(flowId)?.let { flow ->
                    flow.socket.runCatching { close() }
                    flow.readJob?.cancel()
                }
                udpOpenTokens.remove(flowId, token)
                if (!isCurrentGeneration()) {
                    return udpOpenCompletion(BoundedUdpOpenScheduler.Outcome.STALE, openStartedAt)
                }
                if (e is UpstreamNetworkSelector.VpnPolicyException) {
                    logger.warn(tag, "[NETRELAY-VPN] UDP flowId=$flowId blocked: ${e.message}")
                }
                sendFrame(
                    GatewayProtocol.Frame(
                        GatewayProtocol.Type.UDP_OPEN_RESULT,
                        flowId,
                        statusForException(e),
                        payload = (e.message ?: "udp open failed").toByteArray()
                    )
                )
                val resultEnqueuedAt = SystemClock.elapsedRealtime()
                logUdpOpenTrace(
                    request,
                    openStartedAt,
                    socketCreatedAt,
                    networkBoundAt,
                    resultEnqueuedAt
                )
                return BoundedUdpOpenScheduler.Completion(
                    BoundedUdpOpenScheduler.Outcome.FAILED,
                    openStartedAt,
                    resultEnqueuedAt
                )
            }
        }

        private fun udpOpenCompletion(
            outcome: BoundedUdpOpenScheduler.Outcome,
            openStartedAt: Long
        ) = BoundedUdpOpenScheduler.Completion(outcome, openStartedAt, SystemClock.elapsedRealtime())

        private fun isUdpOpenCurrent(flowId: Int, token: Long): Boolean =
            isCurrentGeneration() && udpOpenTokens[flowId] == token

        private fun logUdpOpenTrace(
            request: BoundedUdpOpenScheduler.Request<UdpOpenRequest>,
            openStartedAt: Long,
            socketCreatedAt: Long,
            networkBoundAt: Long,
            resultEnqueuedAt: Long
        ) {
            if (!BuildConfig.DEBUG) return
            val dispatchStartedAt = request.dispatchStartedAtMs
            logger.info(
                tag,
                "[NETRELAY-UDP-OPEN] flowId=${request.flowId} generation=$generation " +
                    "frameReceivedAt=${request.frameReceivedAtMs} dispatchStartedAt=$dispatchStartedAt " +
                    "openStartedAt=$openStartedAt socketCreatedAt=$socketCreatedAt " +
                    "networkBoundAt=$networkBoundAt resultEnqueuedAt=$resultEnqueuedAt " +
                    "frameToDispatchMs=${dispatchStartedAt - request.frameReceivedAtMs} " +
                    "dispatchToOpenMs=${openStartedAt - dispatchStartedAt} " +
                    "openExecutionMs=${resultEnqueuedAt - openStartedAt} " +
                    "openToResultMs=${resultEnqueuedAt - openStartedAt} " +
                    "totalFrameToResultMs=${resultEnqueuedAt - request.frameReceivedAtMs}"
            )
        }

        private suspend fun sendUdp(frame: GatewayProtocol.Frame) {
            val flow = udpFlows[frame.flowId] ?: run {
                sendError(frame.flowId, GatewayProtocol.Status.GENERAL_FAILURE, "Unknown UDP flow")
                return
            }

            try {
                val datagram = try {
                    GatewayProtocol.decodeDatagram(frame.payload)
                } catch (e: ProtocolException) {
                    val hostLength = if (frame.payload.size >= 2) {
                        ((frame.payload[0].toInt() and 0xff) shl 8) or
                            (frame.payload[1].toInt() and 0xff)
                    } else {
                        -1
                    }
                    logger.warn(
                        tag,
                        "[NETRELAY-FRAME] UDP_PARSE_ERROR flow=${frame.flowId} " +
                            "framePayloadLen=${frame.payload.size} offset=0 remaining=${frame.payload.size} " +
                            "hostLen=$hostLength firstHeaderBytes=${GatewayProtocol.metadataHex(frame.payload)}"
                    )
                    throw e
                }
                val address = upstreamNetworkSelector.resolve(flow.egress, datagram.host)
                val packet = DatagramPacket(datagram.data, datagram.data.size, address, datagram.port)
                flow.sendMutex.withLock {
                    flow.socket.send(packet)
                }
            } catch (e: Exception) {
                logger.debug(tag, "UDP send failed flow=${frame.flowId}: ${e.message}")
                sendError(frame.flowId, statusForException(e), e.message ?: "udp send failed")
            }
        }

        private suspend fun readUdp(flowId: Int, flow: UdpFlow) {
            val buffer = ByteArray(UDP_BUFFER_SIZE)
            while (flowIoScope.isActive && !flow.socket.isClosed) {
                try {
                    val packet = DatagramPacket(buffer, buffer.size)
                    flow.socket.receive(packet)
                    val host = packet.address.hostAddress ?: continue
                    val payload = GatewayProtocol.encodeDatagram(
                        host,
                        packet.port,
                        packet.data.copyOfRange(packet.offset, packet.offset + packet.length)
                    )
                    sendFrame(
                        GatewayProtocol.Frame(
                            GatewayProtocol.Type.UDP_DATAGRAM_IN,
                            flowId,
                            payload = payload
                        )
                    )
                } catch (_: SocketTimeoutException) {
                    // Periodic wakeup lets cancellation/close be noticed without killing the association.
                } catch (e: Exception) {
                    if (!flow.socket.isClosed) {
                        logger.debug(tag, "UDP receive ended flow=$flowId: ${e.message}")
                    }
                    break
                }
            }
        }

        private suspend fun closeTcp(flowId: Int, notify: Boolean, cancelWriter: Boolean = true) {
            val flow = tcpFlows.remove(flowId) ?: return
            flow.writeQueue.close()
            flow.socket.runCatching { close() }
            flow.readJob?.cancel()
            if (cancelWriter) flow.writeJob?.cancel()
            if (notify) {
                runCatching {
                    sendFrame(GatewayProtocol.Frame(GatewayProtocol.Type.FLOW_CLOSED, flowId))
                }
            }
        }

        private suspend fun closeUdpInternal(flowId: Int, notify: Boolean) {
            val flow = udpFlows.remove(flowId) ?: return
            flow.socket.runCatching { close() }
            flow.readJob?.cancel()
            if (notify) {
                runCatching {
                    sendFrame(GatewayProtocol.Frame(GatewayProtocol.Type.FLOW_CLOSED, flowId))
                }
            }
        }

        private suspend fun sendError(flowId: Int, status: Int, message: String) {
            sendFrame(
                GatewayProtocol.Frame(
                    GatewayProtocol.Type.ERROR,
                    flowId,
                    status,
                    payload = message.toByteArray()
                )
            )
        }

        private suspend fun sendFrame(frame: GatewayProtocol.Frame): Boolean {
            if (sessionState.get() != SESSION_ACTIVE) {
                logger.debug(tag, "[AEL2-ANDROID] send skipped: session closing flowId=${frame.flowId}")
                return false
            }

            return try {
                outboundWriter?.send(frame) ?: false
            } catch (e: SocketException) {
                logger.debug(tag, "[AEL2-ANDROID] send failed: socket already closed flowId=${frame.flowId}: ${e.message}")
                false
            } catch (e: java.io.IOException) {
                logger.debug(tag, "[AEL2-ANDROID] send failed: disconnected socket flowId=${frame.flowId}: ${e.message}")
                false
            }
        }
    }

    private fun configureTcpSocket(socket: Socket) {
        socket.tcpNoDelay = true
        socket.keepAlive = true
        socket.soTimeout = TCP_IDLE_TIMEOUT_MS
        runCatching { socket.receiveBufferSize = SOCKET_BUFFER_SIZE }
        runCatching { socket.sendBufferSize = SOCKET_BUFFER_SIZE }
    }

    private fun statusForException(error: Exception): Int {
        return when (error) {
            is UnknownHostException -> GatewayProtocol.Status.HOST_UNREACHABLE
            is NoRouteToHostException -> GatewayProtocol.Status.NETWORK_UNREACHABLE
            is ConnectException -> GatewayProtocol.Status.CONNECTION_REFUSED
            is SocketTimeoutException -> GatewayProtocol.Status.TTL_EXPIRED
            else -> GatewayProtocol.Status.GENERAL_FAILURE
        }
    }

    private fun extractHost(endpoint: String?): String? {
        val value = endpoint?.trim().orEmpty()
        if (value.isBlank()) return null
        val host = value.substringBefore(':').trim()
        return host.takeIf { it.isNotBlank() && it != "127.0.0.1" }
    }

    private data class TcpFlow(
        val socket: Socket,
        val writeQueue: Channel<TcpWriteCommand> = Channel(Channel.UNLIMITED),
        var readJob: Job? = null,
        var writeJob: Job? = null
    )

    private data class TcpWriteCommand(
        val payload: ByteArray,
        val close: Boolean
    )

    private data class UdpFlow(
        val socket: DatagramSocket,
        val egress: UpstreamNetworkSelector.EgressNetwork,
        val sendMutex: Mutex = Mutex(),
        var readJob: Job? = null
    )

    private data class UdpOpenRequest(val token: Long)

    companion object {
        const val TCP_CONNECT_MAX_CONCURRENCY = 16
        const val TCP_CONNECT_PENDING_CAPACITY = 64
        const val UDP_OPEN_MAX_CONCURRENCY = 4
        const val UDP_OPEN_PENDING_CAPACITY = 128
        private const val CONTROL_THREAD_COUNT = 2
        private const val UDP_CONTROL_THREAD_COUNT = 4
        private const val UDP_SEND_PENDING_CAPACITY = 512
        const val DEFAULT_PORT = 8991
        internal const val WRITER_MUTEX_WAIT_DEADLINE_MS = 10_000L
        private const val WRITER_MUTEX_SLOW_LOG_MS = 2_000L
        private const val SESSION_ACTIVE = 0
        private const val SESSION_CLOSING = 1
        private const val SESSION_CLOSED = 2
        private const val BUFFER_SIZE = 64 * 1024
        private const val UDP_BUFFER_SIZE = 65_507
        private const val SOCKET_BUFFER_SIZE = 512 * 1024
        private const val CONNECT_TIMEOUT_MS = 15_000
        private const val TCP_IDLE_TIMEOUT_MS = 0 // no read timeout for active TCP flows
        private const val UDP_RECEIVE_TIMEOUT_MS = 2_000
    }
}
