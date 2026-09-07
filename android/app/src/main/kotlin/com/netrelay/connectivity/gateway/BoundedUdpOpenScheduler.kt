package com.netrelay.connectivity.gateway

import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.util.concurrent.ConcurrentSkipListMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * Session-scoped, bounded lane for lightweight UDP socket setup.
 *
 * The workers run only on the dedicated UDP-control dispatcher. Long-lived socket reads and
 * writes must never run here, otherwise a new UDP_OPEN could again wait behind blocked I/O.
 */
internal class BoundedUdpOpenScheduler<T>(
    val sessionGeneration: Long,
    parentJob: Job,
    private val metricsScope: CoroutineScope,
    udpControlDispatcher: CoroutineDispatcher,
    val maxConcurrency: Int,
    val pendingCapacity: Int,
    private val nowElapsedMs: () -> Long,
    private val isCurrentGeneration: () -> Boolean,
    private val execute: suspend (Request<T>) -> Completion,
    private val onSnapshot: (Snapshot) -> Unit = {}
) {
    enum class Admission { SUBMITTED, REJECTED, STALE }
    enum class Outcome { SUCCEEDED, FAILED, STALE }

    data class Request<T>(
        val sequence: Long,
        val sessionGeneration: Long,
        val flowId: Int,
        val value: T,
        val frameReceivedAtMs: Long,
        val enqueuedAtMs: Long,
        var dispatchStartedAtMs: Long = 0
    )

    data class Completion(
        val outcome: Outcome,
        val openStartedAtMs: Long,
        val resultEnqueuedAtMs: Long
    )

    data class Snapshot(
        val sessionGeneration: Long,
        val activeUdpOpens: Int,
        val pendingUdpOpens: Int,
        val maxActiveUdpOpens: Int,
        val maxPendingUdpOpens: Int,
        val udpOpenSucceeded: Long,
        val udpOpenFailed: Long,
        val udpOpenRejected: Long,
        val udpOpenStale: Long,
        val maxFrameToDispatchMs: Long,
        val maxFrameToOpenStartMs: Long,
        val maxOpenExecutionMs: Long,
        val maxFrameToResultMs: Long,
        val oldestPendingAgeMs: Long
    )

    private val active = AtomicBoolean(true)
    private val sequence = AtomicLong(0)
    private val activeOpens = AtomicInteger(0)
    private val pendingOpens = AtomicInteger(0)
    private val maxActiveOpens = AtomicInteger(0)
    private val maxPendingOpens = AtomicInteger(0)
    private val succeeded = AtomicLong(0)
    private val failed = AtomicLong(0)
    private val rejected = AtomicLong(0)
    private val stale = AtomicLong(0)
    private val maxFrameToDispatchMs = AtomicLong(0)
    private val maxFrameToOpenStartMs = AtomicLong(0)
    private val maxOpenExecutionMs = AtomicLong(0)
    private val maxFrameToResultMs = AtomicLong(0)
    private val pendingSince = ConcurrentSkipListMap<Long, Long>()
    private val requests = Channel<Request<T>>(pendingCapacity)
    private val workerJob = SupervisorJob(parentJob)
    private val workerScope = CoroutineScope(workerJob + udpControlDispatcher)

    private val workers = List(maxConcurrency) {
        workerScope.launch { workerLoop() }
    }

    private val metricsJob = metricsScope.launch {
        while (isActive && active.get()) {
            delay(METRICS_INTERVAL_MS)
            onSnapshot(snapshot())
        }
    }

    init {
        require(maxConcurrency > 0)
        require(pendingCapacity > 0)
    }

    fun trySubmit(flowId: Int, value: T, frameReceivedAtMs: Long): Admission {
        if (!active.get() || !isCurrentGeneration()) {
            stale.incrementAndGet()
            return Admission.STALE
        }

        val id = sequence.incrementAndGet()
        val now = nowElapsedMs()
        val request = Request(id, sessionGeneration, flowId, value, frameReceivedAtMs, now)
        pendingSince[id] = now
        val pending = pendingOpens.incrementAndGet()
        updateMax(maxPendingOpens, pending.coerceAtMost(pendingCapacity))

        if (requests.trySend(request).isSuccess) return Admission.SUBMITTED

        pendingSince.remove(id)
        pendingOpens.updateAndGet { (it - 1).coerceAtLeast(0) }
        rejected.incrementAndGet()
        failed.incrementAndGet()
        return Admission.REJECTED
    }

    fun invalidate() {
        if (!active.compareAndSet(true, false)) return
        requests.close()
        metricsJob.cancel()
        workerScope.cancel()
        while (true) {
            val request = requests.tryReceive().getOrNull() ?: break
            pendingSince.remove(request.sequence)
            pendingOpens.updateAndGet { (it - 1).coerceAtLeast(0) }
            stale.incrementAndGet()
        }
    }

    fun snapshot(): Snapshot {
        val now = nowElapsedMs()
        return Snapshot(
            sessionGeneration = sessionGeneration,
            activeUdpOpens = activeOpens.get().coerceAtLeast(0),
            pendingUdpOpens = pendingOpens.get().coerceIn(0, pendingCapacity),
            maxActiveUdpOpens = maxActiveOpens.get().coerceAtMost(maxConcurrency),
            maxPendingUdpOpens = maxPendingOpens.get().coerceAtMost(pendingCapacity),
            udpOpenSucceeded = succeeded.get(),
            udpOpenFailed = failed.get(),
            udpOpenRejected = rejected.get(),
            udpOpenStale = stale.get(),
            maxFrameToDispatchMs = maxFrameToDispatchMs.get(),
            maxFrameToOpenStartMs = maxFrameToOpenStartMs.get(),
            maxOpenExecutionMs = maxOpenExecutionMs.get(),
            maxFrameToResultMs = maxFrameToResultMs.get(),
            oldestPendingAgeMs = pendingSince.firstEntry()?.value?.let {
                (now - it).coerceAtLeast(0)
            } ?: 0
        )
    }

    private suspend fun workerLoop() {
        for (request in requests) {
            pendingSince.remove(request.sequence)
            pendingOpens.updateAndGet { (it - 1).coerceAtLeast(0) }
            val dispatchStartedAt = nowElapsedMs()
            request.dispatchStartedAtMs = dispatchStartedAt
            updateMax(maxFrameToDispatchMs, dispatchStartedAt - request.frameReceivedAtMs)

            if (!active.get() || request.sessionGeneration != sessionGeneration || !isCurrentGeneration()) {
                stale.incrementAndGet()
                continue
            }

            val current = activeOpens.incrementAndGet()
            updateMax(maxActiveOpens, current.coerceAtMost(maxConcurrency))
            try {
                val completion = execute(request)
                updateMax(maxFrameToOpenStartMs, completion.openStartedAtMs - request.frameReceivedAtMs)
                updateMax(maxOpenExecutionMs, completion.resultEnqueuedAtMs - completion.openStartedAtMs)
                updateMax(maxFrameToResultMs, completion.resultEnqueuedAtMs - request.frameReceivedAtMs)
                when (completion.outcome) {
                    Outcome.SUCCEEDED -> succeeded.incrementAndGet()
                    Outcome.FAILED -> failed.incrementAndGet()
                    Outcome.STALE -> stale.incrementAndGet()
                }
            } catch (_: Throwable) {
                failed.incrementAndGet()
            } finally {
                activeOpens.updateAndGet { (it - 1).coerceAtLeast(0) }
            }
        }
    }

    companion object {
        const val METRICS_INTERVAL_MS = 5_000L

        private fun updateMax(target: AtomicInteger, value: Int) {
            var current = target.get()
            while (value > current && !target.compareAndSet(current, value)) current = target.get()
        }

        private fun updateMax(target: AtomicLong, rawValue: Long) {
            val value = rawValue.coerceAtLeast(0)
            var current = target.get()
            while (value > current && !target.compareAndSet(current, value)) current = target.get()
        }
    }
}
