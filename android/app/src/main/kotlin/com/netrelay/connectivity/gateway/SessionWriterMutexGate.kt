package com.netrelay.connectivity.gateway

import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withTimeout
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

internal data class WriterLockOwner(
    val sessionGeneration: Long,
    val attemptId: Long
)

/** Idempotent ownership object; it can release only the exact owner token it represents. */
internal class WriterMutexLease(
    private val mutex: Mutex,
    val owner: WriterLockOwner,
    private val onReleased: (WriterLockOwner) -> Unit = {}
) {
    private val released = AtomicBoolean(false)

    val ownerId: Long
        get() = owner.attemptId

    fun release() {
        if (!released.compareAndSet(false, true)) return
        if (mutex.holdsLock(owner)) {
            mutex.unlock(owner)
        }
        runCatching { onReleased(owner) }
    }
}

/**
 * Per-gateway-session writer serialization. A gate is never shared by two generations.
 * Waiting for [mutex] is cancellable and bounded; blocking OutputStream writes remain guarded
 * separately by the transport writer.
 */
internal class SessionWriterMutexGate(
    val generation: Long,
    val deadlineMs: Long,
    private val isCurrentGeneration: () -> Boolean,
    private val nowElapsedMs: () -> Long,
    private val onMutexWaitStall: (Stall) -> Unit,
    private val onLockEvent: (LockEvent) -> Unit = {}
) {
    enum class WriterPhase {
        IDLE,
        WAITING_FOR_WRITER_MUTEX,
        WRITING_OUTPUT
    }

    data class Stall(
        val generation: Long,
        val ownerId: Long,
        val waitMs: Long,
        val opcode: Int,
        val flowId: Int,
        val currentWriterGeneration: Long,
        val currentWriterPhase: WriterPhase,
        val heldLockBeforeCleanup: Boolean
    )

    sealed class LockEvent {
        abstract val generation: Long
        abstract val ownerId: Long

        data class Granted(
            override val generation: Long,
            override val ownerId: Long,
            val waitMs: Long
        ) : LockEvent()

        data class TimedOut(
            override val generation: Long,
            override val ownerId: Long,
            val heldLockBeforeCleanup: Boolean
        ) : LockEvent()

        data class TimedOutOwnedLockReleased(
            override val generation: Long,
            override val ownerId: Long
        ) : LockEvent()

        data class LeaseReleased(
            override val generation: Long,
            override val ownerId: Long
        ) : LockEvent()
    }

    sealed class Result<out T> {
        data class Completed<T>(val value: T, val waitMs: Long) : Result<T>()
        data class Stale(val waitMs: Long) : Result<Nothing>()
        data class TimedOut(val stall: Stall) : Result<Nothing>()
    }

    private val mutex = Mutex()
    private val active = AtomicBoolean(true)
    private val stallSignalled = AtomicBoolean(false)
    private val waiterCount = AtomicInteger(0)
    private val phase = AtomicReference(WriterPhase.IDLE)
    private val writerGeneration = AtomicLong(NO_GENERATION)
    private val ownerSequence = AtomicLong(0)

    private sealed class AcquireResult {
        data class Acquired(
            val lease: WriterMutexLease,
            val waitMs: Long
        ) : AcquireResult()

        data class TimedOut(
            val ownerId: Long,
            val waitMs: Long,
            val heldLockBeforeCleanup: Boolean
        ) : AcquireResult()
    }

    suspend fun <T> execute(
        opcode: Int,
        flowId: Int,
        block: suspend () -> T
    ): Result<T> {
        if (!active.get() || !isCurrentGeneration()) return Result.Stale(0)

        val acquisition = acquireWithTimeout()
        if (acquisition is AcquireResult.TimedOut) {
            val stall = Stall(
                generation = generation,
                ownerId = acquisition.ownerId,
                waitMs = acquisition.waitMs,
                opcode = opcode,
                flowId = flowId,
                currentWriterGeneration = writerGeneration.get(),
                currentWriterPhase = phase.get(),
                heldLockBeforeCleanup = acquisition.heldLockBeforeCleanup
            )
            if (active.get() && isCurrentGeneration() && stallSignalled.compareAndSet(false, true)) {
                onMutexWaitStall(stall)
            }
            return Result.TimedOut(stall)
        }

        acquisition as AcquireResult.Acquired
        val lease = acquisition.lease
        val waitMs = acquisition.waitMs
        try {
            if (!active.get() || !isCurrentGeneration()) return Result.Stale(waitMs)
            writerGeneration.set(generation)
            phase.set(WriterPhase.WRITING_OUTPUT)
            return Result.Completed(block(), waitMs)
        } finally {
            writerGeneration.compareAndSet(generation, NO_GENERATION)
            lease.release()
            phase.set(if (waiterCount.get() > 0) {
                WriterPhase.WAITING_FOR_WRITER_MUTEX
            } else {
                WriterPhase.IDLE
            })
        }
    }

    /**
     * Returns only a committed owner-token lease. Timeout/cancellation cleanup is performed
     * against that exact owner token, so the timeout/grant boundary cannot orphan the mutex.
     */
    private suspend fun acquireWithTimeout(): AcquireResult {
        val owner = WriterLockOwner(generation, ownerSequence.incrementAndGet())
        val waitStarted = nowElapsedMs()
        waiterCount.incrementAndGet()
        phase.compareAndSet(WriterPhase.IDLE, WriterPhase.WAITING_FOR_WRITER_MUTEX)
        try {
            try {
                withTimeout(deadlineMs) {
                    mutex.lock(owner)
                }
            } catch (timeout: TimeoutCancellationException) {
                val heldBeforeCleanup = mutex.holdsLock(owner)
                if (heldBeforeCleanup) {
                    mutex.unlock(owner)
                }
                // Diagnostics run only after ownership cleanup and can never prevent release.
                runCatching {
                    onLockEvent(LockEvent.TimedOut(generation, owner.attemptId, heldBeforeCleanup))
                    if (heldBeforeCleanup) {
                        onLockEvent(LockEvent.TimedOutOwnedLockReleased(generation, owner.attemptId))
                    }
                }
                return AcquireResult.TimedOut(
                    ownerId = owner.attemptId,
                    waitMs = (nowElapsedMs() - waitStarted).coerceAtLeast(0),
                    heldLockBeforeCleanup = heldBeforeCleanup
                )
            } catch (cancelled: CancellationException) {
                // Mutex.lock is cancellable. If cancellation won at the grant boundary, only
                // this attempt's owner token may release the newly granted lock.
                if (mutex.holdsLock(owner)) {
                    mutex.unlock(owner)
                }
                throw cancelled
            }

            val waitMs = (nowElapsedMs() - waitStarted).coerceAtLeast(0)
            check(mutex.holdsLock(owner)) {
                "Writer mutex acquisition returned without owner-token ownership"
            }
            val lease = WriterMutexLease(mutex, owner) { releasedOwner ->
                    onLockEvent(LockEvent.LeaseReleased(generation, releasedOwner.attemptId))
                }
            // A logging callback must not be able to orphan an already granted lock.
            runCatching { onLockEvent(LockEvent.Granted(generation, owner.attemptId, waitMs)) }
            return AcquireResult.Acquired(lease, waitMs)
        } finally {
            waiterCount.decrementAndGet()
            if (!mutex.isLocked && writerGeneration.get() == NO_GENERATION) {
                phase.set(if (waiterCount.get() > 0) {
                    WriterPhase.WAITING_FOR_WRITER_MUTEX
                } else {
                    WriterPhase.IDLE
                })
            }
        }
    }

    fun invalidate(): Int {
        active.set(false)
        return waiterCount.get()
    }

    fun pendingWaiters(): Int = waiterCount.get()

    internal fun isLockedForTest(): Boolean = mutex.isLocked

    companion object {
        private const val NO_GENERATION = -1L
    }
}
