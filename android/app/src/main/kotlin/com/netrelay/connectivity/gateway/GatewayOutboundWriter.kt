package com.netrelay.connectivity.gateway

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.selects.select
import kotlinx.coroutines.delay
import java.util.concurrent.ConcurrentSkipListMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * Session-local AEL2 outbound serializer.
 *
 * Producers enqueue immutable encoded frames. Exactly one coroutine touches the output stream.
 * Queue capacity is deliberately small so TCP backpressure reaches the remote socket instead of
 * becoming unbounded Android heap usage. UDP payload is allowed to drop under congestion, while
 * protocol and flow-control frames always suspend until accepted or the session is cancelled.
 */
internal class GatewayOutboundWriter(
    val sessionGeneration: Long,
    private val scope: CoroutineScope,
    private val isCurrentGeneration: () -> Boolean,
    private val writeEncoded: suspend (QueuedFrame) -> Unit,
    private val nowElapsedMs: () -> Long,
    private val onSnapshot: (Snapshot) -> Unit = {},
    private val onSlowQueue: (SlowQueueEvent) -> Unit = {},
    private val onWriteFailure: (Throwable) -> Unit = {},
    controlCapacity: Int = CONTROL_CAPACITY,
    flowControlCapacity: Int = FLOW_CONTROL_CAPACITY,
    dataCapacity: Int = DATA_CAPACITY
) {
    enum class Priority { CONTROL, FLOW_CONTROL, DATA }

    data class QueuedFrame(
        val sequence: Long,
        val type: Int,
        val flowId: Int,
        val arg: Int,
        val priority: Priority,
        val encoded: ByteArray,
        @Volatile var enqueuedAtMs: Long,
        val isUdpData: Boolean,
        val completion: CompletableDeferred<Boolean> = CompletableDeferred()
    ) {
        private val tracked = AtomicBoolean(false)
        private val consumedBeforeTracking = AtomicBoolean(false)

        fun markTracked(action: () -> Unit) = synchronized(this) {
            if (!consumedBeforeTracking.get()) {
                action()
                tracked.set(true)
            }
        }

        fun markConsumed(action: () -> Unit) = synchronized(this) {
            if (tracked.get()) action() else consumedBeforeTracking.set(true)
        }
    }

    data class Snapshot(
        val generation: Long,
        val controlDepth: Int,
        val flowControlDepth: Int,
        val dataDepth: Int,
        val controlOldestAgeMs: Long,
        val flowControlOldestAgeMs: Long,
        val dataOldestAgeMs: Long,
        val enqueuedFrames: Long,
        val dequeuedFrames: Long,
        val tcpBackpressureCount: Long,
        val udpDroppedCount: Long,
        val writerRunning: Boolean,
        val writerLastProgressAgeMs: Long,
        val maxControlAgeMs: Long,
        val maxFlowControlAgeMs: Long,
        val maxDataAgeMs: Long,
        val maxControlDepth: Int,
        val maxFlowControlDepth: Int,
        val maxDataDepth: Int,
        val maxPongLatencyMs: Long,
        val maxOpenResultLatencyMs: Long
    )

    data class SlowQueueEvent(
        val generation: Long,
        val priority: Priority,
        val type: Int,
        val flowId: Int,
        val ageMs: Long
    )

    private class QueueStats(private val capacity: Int) {
        val pending = ConcurrentSkipListMap<Long, Long>()
        val depth = AtomicInteger(0)
        val maxDepth = AtomicInteger(0)
        val maxAgeMs = AtomicLong(0)

        fun track(item: QueuedFrame) {
            pending[item.sequence] = item.enqueuedAtMs
            val current = depth.incrementAndGet()
            updateMax(maxDepth, current.coerceAtMost(capacity))
        }

        fun consume(item: QueuedFrame) {
            pending.remove(item.sequence)
            depth.updateAndGet { value -> (value - 1).coerceAtLeast(0) }
        }

        fun oldestAge(now: Long): Long = pending.firstEntry()?.value?.let {
            (now - it).coerceAtLeast(0)
        } ?: 0

        fun boundedDepth(): Int = depth.get().coerceAtMost(capacity)
    }

    private val active = AtomicBoolean(true)
    private val sequence = AtomicLong(0)
    private val enqueuedFrames = AtomicLong(0)
    private val dequeuedFrames = AtomicLong(0)
    private val tcpBackpressureCount = AtomicLong(0)
    private val udpDroppedCount = AtomicLong(0)
    private val lastProgressMs = AtomicLong(nowElapsedMs())
    private val maxPongLatencyMs = AtomicLong(0)
    private val maxOpenResultLatencyMs = AtomicLong(0)

    private val control = Channel<QueuedFrame>(controlCapacity)
    private val flowControl = Channel<QueuedFrame>(flowControlCapacity)
    private val data = Channel<QueuedFrame>(dataCapacity)
    private val controlStats = QueueStats(controlCapacity)
    private val flowControlStats = QueueStats(flowControlCapacity)
    private val dataStats = QueueStats(dataCapacity)

    private val writerJob: Job = scope.launch { writerLoop() }
    private val snapshotJob: Job = scope.launch {
        while (isActive && active.get()) {
            delay(SNAPSHOT_INTERVAL_MS)
            onSnapshot(snapshot())
        }
    }

    suspend fun send(frame: GatewayProtocol.Frame): Boolean {
        if (!active.get() || !isCurrentGeneration()) return false
        val owned = frame.ownedCopy()
        val priority = priorityFor(owned.type)
        val item = QueuedFrame(
            sequence = sequence.incrementAndGet(),
            type = owned.type,
            flowId = owned.flowId,
            arg = owned.arg,
            priority = priority,
            encoded = GatewayProtocol.encodeFrame(owned),
            enqueuedAtMs = nowElapsedMs(),
            isUdpData = owned.type == GatewayProtocol.Type.UDP_DATAGRAM_IN
        )
        val channel = channelFor(priority)
        val stats = statsFor(priority)

        val accepted = if (item.isUdpData) {
            channel.trySend(item).isSuccess.also { success ->
                if (!success) {
                    udpDroppedCount.incrementAndGet()
                    item.completion.complete(false)
                }
            }
        } else {
            if (priority == Priority.DATA && channel.trySend(item).isFailure) {
                tcpBackpressureCount.incrementAndGet()
                try {
                    channel.send(item)
                    true
                } catch (_: CancellationException) {
                    false
                } catch (_: Throwable) {
                    false
                }
            } else if (priority != Priority.DATA) {
                try {
                    channel.send(item)
                    true
                } catch (_: CancellationException) {
                    false
                } catch (_: Throwable) {
                    false
                }
            } else {
                true
            }
        }

        if (!accepted) return false
        item.enqueuedAtMs = nowElapsedMs()
        item.markTracked {
            stats.track(item)
            enqueuedFrames.incrementAndGet()
        }
        return try {
            item.completion.await()
        } catch (_: CancellationException) {
            false
        }
    }

    fun invalidate() {
        if (!active.compareAndSet(true, false)) return
        control.close()
        flowControl.close()
        data.close()
        writerJob.cancel()
        snapshotJob.cancel()
        drain(control)
        drain(flowControl)
        drain(data)
    }

    fun snapshot(): Snapshot {
        val now = nowElapsedMs()
        return Snapshot(
            generation = sessionGeneration,
            controlDepth = controlStats.boundedDepth(),
            flowControlDepth = flowControlStats.boundedDepth(),
            dataDepth = dataStats.boundedDepth(),
            controlOldestAgeMs = controlStats.oldestAge(now),
            flowControlOldestAgeMs = flowControlStats.oldestAge(now),
            dataOldestAgeMs = dataStats.oldestAge(now),
            enqueuedFrames = enqueuedFrames.get(),
            dequeuedFrames = dequeuedFrames.get(),
            tcpBackpressureCount = tcpBackpressureCount.get(),
            udpDroppedCount = udpDroppedCount.get(),
            writerRunning = writerJob.isActive,
            writerLastProgressAgeMs = (now - lastProgressMs.get()).coerceAtLeast(0),
            maxControlAgeMs = controlStats.maxAgeMs.get(),
            maxFlowControlAgeMs = flowControlStats.maxAgeMs.get(),
            maxDataAgeMs = dataStats.maxAgeMs.get(),
            maxControlDepth = controlStats.maxDepth.get(),
            maxFlowControlDepth = flowControlStats.maxDepth.get(),
            maxDataDepth = dataStats.maxDepth.get(),
            maxPongLatencyMs = maxPongLatencyMs.get(),
            maxOpenResultLatencyMs = maxOpenResultLatencyMs.get()
        )
    }

    private suspend fun writerLoop() {
        var priorityFramesSinceData = 0
        var controlFramesSinceFlowControl = 0
        try {
            while (scope.isActive && active.get() && isCurrentGeneration()) {
                val item = pollNext(priorityFramesSinceData, controlFramesSinceFlowControl)
                    ?: receiveNext(priorityFramesSinceData, controlFramesSinceFlowControl)
                    ?: continue
                item.markConsumed { statsFor(item.priority).consume(item) }
                val ageMs = (nowElapsedMs() - item.enqueuedAtMs).coerceAtLeast(0)
                updateMax(statsFor(item.priority).maxAgeMs, ageMs)
                if (item.priority == Priority.CONTROL && ageMs > CONTROL_WARN_AGE_MS ||
                    item.priority == Priority.FLOW_CONTROL && ageMs > FLOW_CONTROL_WARN_AGE_MS
                ) {
                    onSlowQueue(SlowQueueEvent(sessionGeneration, item.priority, item.type, item.flowId, ageMs))
                }
                if (item.type == GatewayProtocol.Type.PONG) updateMax(maxPongLatencyMs, ageMs)
                if (item.type == GatewayProtocol.Type.TCP_CONNECT_RESULT ||
                    item.type == GatewayProtocol.Type.UDP_OPEN_RESULT
                ) updateMax(maxOpenResultLatencyMs, ageMs)

                try {
                    writeEncoded(item)
                    lastProgressMs.set(nowElapsedMs())
                    dequeuedFrames.incrementAndGet()
                    item.completion.complete(true)
                } catch (cancelled: CancellationException) {
                    item.completion.complete(false)
                    throw cancelled
                } catch (failure: Throwable) {
                    item.completion.complete(false)
                    if (active.get() && isCurrentGeneration()) onWriteFailure(failure)
                    break
                }

                when (item.priority) {
                    Priority.CONTROL -> {
                        priorityFramesSinceData++
                        controlFramesSinceFlowControl++
                    }
                    Priority.FLOW_CONTROL -> {
                        priorityFramesSinceData++
                        controlFramesSinceFlowControl = 0
                    }
                    Priority.DATA -> {
                        priorityFramesSinceData = 0
                        controlFramesSinceFlowControl = 0
                    }
                }
            }
        } finally {
            active.set(false)
            control.close()
            flowControl.close()
            data.close()
            drain(control)
            drain(flowControl)
            drain(data)
        }
    }

    private fun pollNext(priorityBurst: Int, controlBurst: Int): QueuedFrame? {
        if (priorityBurst >= MAX_PRIORITY_BURST) data.tryReceive().getOrNull()?.let { return it }
        if (controlBurst >= MAX_CONTROL_BURST) flowControl.tryReceive().getOrNull()?.let { return it }
        control.tryReceive().getOrNull()?.let { return it }
        flowControl.tryReceive().getOrNull()?.let { return it }
        return data.tryReceive().getOrNull()
    }

    private suspend fun receiveNext(priorityBurst: Int, controlBurst: Int): QueuedFrame? = when {
        priorityBurst >= MAX_PRIORITY_BURST -> select {
            data.onReceiveCatching { it.getOrNull() }
            control.onReceiveCatching { it.getOrNull() }
            flowControl.onReceiveCatching { it.getOrNull() }
        }
        controlBurst >= MAX_CONTROL_BURST -> select {
            flowControl.onReceiveCatching { it.getOrNull() }
            control.onReceiveCatching { it.getOrNull() }
            data.onReceiveCatching { it.getOrNull() }
        }
        else -> select {
            control.onReceiveCatching { it.getOrNull() }
            flowControl.onReceiveCatching { it.getOrNull() }
            data.onReceiveCatching { it.getOrNull() }
        }
    }

    private fun channelFor(priority: Priority): Channel<QueuedFrame> = when (priority) {
        Priority.CONTROL -> control
        Priority.FLOW_CONTROL -> flowControl
        Priority.DATA -> data
    }

    private fun statsFor(priority: Priority): QueueStats = when (priority) {
        Priority.CONTROL -> controlStats
        Priority.FLOW_CONTROL -> flowControlStats
        Priority.DATA -> dataStats
    }

    private fun drain(channel: Channel<QueuedFrame>) {
        while (true) {
            val item = channel.tryReceive().getOrNull() ?: break
            item.markConsumed { statsFor(item.priority).consume(item) }
            item.completion.complete(false)
        }
    }

    companion object {
        const val CONTROL_CAPACITY = 32
        const val FLOW_CONTROL_CAPACITY = 128
        const val DATA_CAPACITY = 32
        const val MAX_CONTROL_BURST = 4
        const val MAX_PRIORITY_BURST = 8
        const val SNAPSHOT_INTERVAL_MS = 5_000L
        const val CONTROL_WARN_AGE_MS = 1_000L
        const val FLOW_CONTROL_WARN_AGE_MS = 2_000L

        fun priorityFor(type: Int): Priority = when (type) {
            GatewayProtocol.Type.HELLO_ACK,
            GatewayProtocol.Type.PONG -> Priority.CONTROL

            GatewayProtocol.Type.TCP_CONNECT_RESULT,
            GatewayProtocol.Type.UDP_OPEN_RESULT,
            GatewayProtocol.Type.ERROR -> Priority.FLOW_CONTROL

            GatewayProtocol.Type.TCP_DATA_IN,
            GatewayProtocol.Type.UDP_DATAGRAM_IN,
            // FLOW_CLOSED shares the ordered data lane so it cannot jump over payload
            // already accepted for the same flow. Unlike UDP payload, it is never dropped.
            GatewayProtocol.Type.FLOW_CLOSED -> Priority.DATA

            else -> Priority.FLOW_CONTROL
        }

        private fun updateMax(target: AtomicLong, value: Long) {
            var current = target.get()
            while (value > current && !target.compareAndSet(current, value)) current = target.get()
        }

        private fun updateMax(target: AtomicInteger, value: Int) {
            var current = target.get()
            while (value > current && !target.compareAndSet(current, value)) current = target.get()
        }
    }
}
