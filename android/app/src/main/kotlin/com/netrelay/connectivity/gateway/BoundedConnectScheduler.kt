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
 * Session-scoped admission and execution lane for blocking outbound connects.
 *
 * Only [maxConcurrency] workers execute on [connectDispatcher]. Pending work is bounded by
 * [pendingCapacity], and metrics run on [metricsScope] so saturated connect threads cannot hide
 * their own saturation. A scheduler belongs to exactly one gateway generation.
 */
internal class BoundedConnectScheduler<T>(
    val sessionGeneration: Long,
    parentJob: Job,
    private val metricsScope: CoroutineScope,
    connectDispatcher: CoroutineDispatcher,
    val maxConcurrency: Int,
    val pendingCapacity: Int,
    private val nowElapsedMs: () -> Long,
    private val isCurrentGeneration: () -> Boolean,
    private val execute: suspend (Request<T>) -> Outcome,
    private val onSnapshot: (Snapshot) -> Unit = {}
) {
    enum class Outcome { SUCCEEDED, TIMED_OUT, FAILED, STALE }

    data class Request<T>(
        val sequence: Long,
        val sessionGeneration: Long,
        val flowId: Int,
        val value: T,
        val enqueuedAtMs: Long
    )

    data class Snapshot(
        val sessionGeneration: Long,
        val activeTcpConnects: Int,
        val pendingTcpConnects: Int,
        val maxConcurrentTcpConnects: Int,
        val maxPendingTcpConnects: Int,
        val connectPermitsAvailable: Int,
        val connectSucceeded: Long,
        val connectTimedOut: Long,
        val connectFailed: Long,
        val connectRejected: Long,
        val connectStale: Long,
        val connectOldestPendingAgeMs: Long
    )

    private val active = AtomicBoolean(true)
    private val sequence = AtomicLong(0)
    private val activeConnects = AtomicInteger(0)
    private val pendingConnects = AtomicInteger(0)
    private val maxActiveConnects = AtomicInteger(0)
    private val maxPendingConnects = AtomicInteger(0)
    private val succeeded = AtomicLong(0)
    private val timedOut = AtomicLong(0)
    private val failed = AtomicLong(0)
    private val rejected = AtomicLong(0)
    private val stale = AtomicLong(0)
    private val pendingSince = ConcurrentSkipListMap<Long, Long>()
    private val requests = Channel<Request<T>>(pendingCapacity)
    private val workerJob = SupervisorJob(parentJob)
    private val workerScope = CoroutineScope(workerJob + connectDispatcher)

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

    fun trySubmit(flowId: Int, value: T): Boolean {
        if (!active.get() || !isCurrentGeneration()) {
            stale.incrementAndGet()
            return false
        }

        val id = sequence.incrementAndGet()
        val now = nowElapsedMs()
        val request = Request(id, sessionGeneration, flowId, value, now)
        pendingSince[id] = now
        val pending = pendingConnects.incrementAndGet()
        updateMax(maxPendingConnects, pending.coerceAtMost(pendingCapacity))

        if (requests.trySend(request).isSuccess) return true

        pendingSince.remove(id)
        pendingConnects.updateAndGet { (it - 1).coerceAtLeast(0) }
        rejected.incrementAndGet()
        failed.incrementAndGet()
        return false
    }

    fun invalidate() {
        if (!active.compareAndSet(true, false)) return
        requests.close()
        metricsJob.cancel()
        workerScope.cancel()
        while (true) {
            val request = requests.tryReceive().getOrNull() ?: break
            pendingSince.remove(request.sequence)
            pendingConnects.updateAndGet { (it - 1).coerceAtLeast(0) }
            stale.incrementAndGet()
        }
    }

    fun snapshot(): Snapshot {
        val now = nowElapsedMs()
        val currentActive = activeConnects.get().coerceAtLeast(0)
        return Snapshot(
            sessionGeneration = sessionGeneration,
            activeTcpConnects = currentActive,
            pendingTcpConnects = pendingConnects.get().coerceIn(0, pendingCapacity),
            maxConcurrentTcpConnects = maxActiveConnects.get().coerceAtMost(maxConcurrency),
            maxPendingTcpConnects = maxPendingConnects.get().coerceAtMost(pendingCapacity),
            connectPermitsAvailable = (maxConcurrency - currentActive).coerceAtLeast(0),
            connectSucceeded = succeeded.get(),
            connectTimedOut = timedOut.get(),
            connectFailed = failed.get(),
            connectRejected = rejected.get(),
            connectStale = stale.get(),
            connectOldestPendingAgeMs = pendingSince.firstEntry()?.value?.let {
                (now - it).coerceAtLeast(0)
            } ?: 0
        )
    }

    private suspend fun workerLoop() {
        for (request in requests) {
            pendingSince.remove(request.sequence)
            pendingConnects.updateAndGet { (it - 1).coerceAtLeast(0) }

            if (!active.get() || request.sessionGeneration != sessionGeneration || !isCurrentGeneration()) {
                stale.incrementAndGet()
                continue
            }

            val current = activeConnects.incrementAndGet()
            updateMax(maxActiveConnects, current.coerceAtMost(maxConcurrency))
            try {
                when (execute(request)) {
                    Outcome.SUCCEEDED -> succeeded.incrementAndGet()
                    Outcome.TIMED_OUT -> timedOut.incrementAndGet()
                    Outcome.FAILED -> failed.incrementAndGet()
                    Outcome.STALE -> stale.incrementAndGet()
                }
            } catch (_: Throwable) {
                failed.incrementAndGet()
            } finally {
                activeConnects.updateAndGet { (it - 1).coerceAtLeast(0) }
            }
        }
    }

    companion object {
        const val METRICS_INTERVAL_MS = 5_000L

        private fun updateMax(target: AtomicInteger, value: Int) {
            var current = target.get()
            while (value > current && !target.compareAndSet(current, value)) current = target.get()
        }
    }
}
