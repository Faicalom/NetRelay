package com.netrelay.connectivity.gateway

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.delay
import kotlinx.coroutines.joinAll
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.sync.Mutex
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

class SessionWriterMutexGateTest {
    @Test
    fun testA_normalAcquireRelease_leavesMutexAvailable() = runBlocking {
        val current = AtomicLong(1)
        val gate = gate(1, current, 1_000)
        val result = gate.execute(GatewayProtocol.Type.PONG, 0) { "ok" }
        assertEquals("ok", (result as SessionWriterMutexGate.Result.Completed).value)
        assertFalse(gate.isLockedForTest())
    }

    @Test
    fun testA_sessionLocalMutex_oldGenerationCannotBlockFreshPong() = runBlocking {
        val current = AtomicLong(1)
        val oldEntered = CompletableDeferred<Unit>()
        val releaseOld = CompletableDeferred<Unit>()
        val old = gate(1, current, 5_000)
        val oldWrite = async(Dispatchers.Default) {
            old.execute(GatewayProtocol.Type.TCP_DATA_IN, 1) {
                oldEntered.complete(Unit)
                releaseOld.await()
            }
        }
        oldEntered.await()

        current.set(2)
        val fresh = gate(2, current, 5_000)
        val result = withTimeout(500) {
            fresh.execute(GatewayProtocol.Type.PONG, 0) { "pong" }
        }
        assertEquals("pong", (result as SessionWriterMutexGate.Result.Completed).value)

        releaseOld.complete(Unit)
        oldWrite.await()
        Unit
    }

    @Test
    fun testB_mutexWaitTimeout_triggersSingleCurrentGenerationAbort() = runBlocking {
        val current = AtomicLong(1)
        val stallCount = AtomicInteger(0)
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val gate = gate(1, current, 100) { stallCount.incrementAndGet() }
        val holder = async(Dispatchers.Default) {
            gate.execute(GatewayProtocol.Type.TCP_DATA_IN, 1) {
                entered.complete(Unit)
                release.await()
            }
        }
        entered.await()

        val timedOut = gate.execute(GatewayProtocol.Type.PONG, 0) { Unit }
        assertTrue(timedOut is SessionWriterMutexGate.Result.TimedOut)
        assertEquals(1, stallCount.get())

        release.complete(Unit)
        holder.await()
        val next = withTimeout(500) {
            gate.execute(GatewayProtocol.Type.PONG, 2) { "usable" }
        }
        assertEquals("usable", (next as SessionWriterMutexGate.Result.Completed).value)
        assertFalse(gate.isLockedForTest())
        Unit
    }

    @Test
    fun testC_cancellingSessionCancelsOneHundredMutexWaiters() = runBlocking {
        val current = AtomicLong(1)
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val gate = gate(1, current, 10_000)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val holder = scope.async {
            gate.execute(GatewayProtocol.Type.TCP_DATA_IN, 1) {
                entered.complete(Unit)
                release.await()
            }
        }
        entered.await()
        val waiters = List(100) {
            scope.launch { gate.execute(GatewayProtocol.Type.UDP_DATAGRAM_IN, it) { Unit } }
        }
        delay(100)
        waiters.forEach { it.cancel() }
        waiters.joinAll()
        assertEquals(0, gate.pendingWaiters())
        release.complete(Unit)
        holder.await()
        val next = withTimeout(500) {
            gate.execute(GatewayProtocol.Type.PONG, 200) { Unit }
        }
        assertTrue(next is SessionWriterMutexGate.Result.Completed)
        assertFalse(gate.isLockedForTest())
        Unit
        scope.cancel()
    }

    @Test
    fun testD_timeoutGrantBoundaryRace_fiftyThousandAttemptsNeverLeakMutex() = runBlocking {
        val attemptsPerBatch = 100
        val batches = 500
        var flowId = 0

        repeat(batches) { batch ->
            val generation = batch.toLong() + 1
            val current = AtomicLong(generation)
            val gate = gate(generation, current, 3)
            val holderEntered = CompletableDeferred<Unit>()
            val releaseHolder = CompletableDeferred<Unit>()
            val holder = async(Dispatchers.Default) {
                gate.execute(GatewayProtocol.Type.TCP_DATA_IN, flowId++) {
                    holderEntered.complete(Unit)
                    releaseHolder.await()
                }
            }
            holderEntered.await()

            val waiters = List(attemptsPerBatch) {
                async(Dispatchers.Default) {
                    gate.execute(GatewayProtocol.Type.UDP_DATAGRAM_IN, flowId++) { Unit }
                }
            }
            // Release close to the timeout boundary. Depending on scheduling, individual
            // attempts either receive the owner token or time out while grant is racing.
            delay(if (batch % 2 == 0) 1 else 2)
            releaseHolder.complete(Unit)
            holder.await()
            waiters.awaitAll()

            val third = withTimeout(250) {
                gate.execute(GatewayProtocol.Type.PONG, flowId++) { Unit }
            }
            assertTrue("batch=$batch leaked writer mutex", third is SessionWriterMutexGate.Result.Completed)
            assertFalse("batch=$batch mutex remained locked", gate.isLockedForTest())
        }

        assertEquals(50_000, attemptsPerBatch * batches)
    }

    @Test
    fun testE_cancelledWaiterNeverOwnsMutexAfterCancellation() = runBlocking {
        val current = AtomicLong(1)
        val gate = gate(1, current, 10_000)
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val holder = async(Dispatchers.Default) {
            gate.execute(GatewayProtocol.Type.TCP_DATA_IN, 1) {
                entered.complete(Unit)
                release.await()
            }
        }
        entered.await()
        val waiter = launch(Dispatchers.Default) {
            gate.execute(GatewayProtocol.Type.PONG, 2) { Unit }
        }
        delay(20)
        waiter.cancelAndJoin()
        release.complete(Unit)
        holder.await()
        assertFalse(gate.isLockedForTest())
    }

    @Test
    fun testF_leaseDoubleRelease_isIdempotentAndCannotUnlockAnotherOwner() = runBlocking {
        val mutex = Mutex()
        val firstOwner = WriterLockOwner(1, 1)
        mutex.lock(firstOwner)
        val lease = WriterMutexLease(mutex, firstOwner)
        lease.release()
        lease.release()
        assertFalse(mutex.isLocked)

        val secondOwner = WriterLockOwner(1, 2)
        withTimeout(250) { mutex.lock(secondOwner) }
        lease.release()
        assertTrue(mutex.holdsLock(secondOwner))
        mutex.unlock(secondOwner)
    }

    @Test
    fun testG_oldGenerationLeaseCannotUnlockNewSessionMutex() = runBlocking {
        val oldMutex = Mutex()
        val freshMutex = Mutex()
        val oldOwner = WriterLockOwner(1, 1)
        val freshOwner = WriterLockOwner(2, 1)
        oldMutex.lock(oldOwner)
        freshMutex.lock(freshOwner)
        val oldLease = WriterMutexLease(oldMutex, oldOwner)

        oldLease.release()
        assertFalse(oldMutex.isLocked)
        assertTrue(freshMutex.holdsLock(freshOwner))
        freshMutex.unlock(freshOwner)
    }

    @Test
    fun testH_oldWriterCannotWriteIntoFreshSessionOutput() = runBlocking {
        val current = AtomicLong(1)
        val oldOutput = StringBuilder()
        val freshOutput = StringBuilder()
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val old = gate(1, current, 5_000)
        val oldWrite = async(Dispatchers.Default) {
            old.execute(GatewayProtocol.Type.TCP_DATA_IN, 1) {
                entered.complete(Unit)
                release.await()
                oldOutput.append("old")
            }
        }
        entered.await()
        current.set(2)
        val fresh = gate(2, current, 5_000)
        fresh.execute(GatewayProtocol.Type.PONG, 0) { freshOutput.append("fresh") }
        release.complete(Unit)
        oldWrite.await()
        assertEquals("fresh", freshOutput.toString())
        assertEquals("old", oldOutput.toString())
    }

    @Test
    fun testI_staleMutexWatchdogCannotAbortFreshGeneration() = runBlocking {
        val current = AtomicLong(1)
        val aborts = AtomicInteger(0)
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val old = gate(1, current, 100) { aborts.incrementAndGet() }
        val holder = async(Dispatchers.Default) {
            old.execute(GatewayProtocol.Type.TCP_DATA_IN, 1) {
                entered.complete(Unit)
                release.await()
            }
        }
        entered.await()
        val waiter = async(Dispatchers.Default) {
            old.execute(GatewayProtocol.Type.PONG, 0) { Unit }
        }
        delay(25)
        current.set(2)
        assertTrue(waiter.await() is SessionWriterMutexGate.Result.TimedOut)
        assertEquals(0, aborts.get())
        release.complete(Unit)
        holder.await()
        Unit
    }

    @Test
    fun testK_deadlinesRemainTenSecondsAndHeartbeatTimeoutIsNotChangedHere() {
        assertEquals(10_000L, SocketGatewayServer.WRITER_MUTEX_WAIT_DEADLINE_MS)
    }

    @Test
    fun testL_ael2FrameAndUdpIntegrityRoundTrip() {
        val datagram = GatewayProtocol.encodeDatagram("1.1.1.1", 53, byteArrayOf(1, 2, 3))
        val frame = GatewayProtocol.Frame(
            type = GatewayProtocol.Type.UDP_DATAGRAM_IN,
            flowId = 7,
            payload = datagram
        )
        val decodedFrame = GatewayProtocol.readFrame(GatewayProtocol.encodeFrame(frame).inputStream())!!
        val decodedDatagram = GatewayProtocol.decodeDatagram(decodedFrame.payload)
        assertEquals(frame.type, decodedFrame.type)
        assertEquals(frame.flowId, decodedFrame.flowId)
        assertEquals("1.1.1.1", decodedDatagram.host)
        assertEquals(53, decodedDatagram.port)
        assertTrue(byteArrayOf(1, 2, 3).contentEquals(decodedDatagram.data))
    }

    @Test
    fun testM_transportIndependentGateHasNoUsbOrWifiDependencies() {
        val dependencyNames = SessionWriterMutexGate::class.java.declaredFields
            .map { it.type.name }
            .joinToString(" ")
        assertFalse(dependencyNames.contains("usb", ignoreCase = true))
        assertFalse(dependencyNames.contains("wifi", ignoreCase = true))
    }

    private fun gate(
        generation: Long,
        current: AtomicLong,
        deadlineMs: Long,
        onStall: (SessionWriterMutexGate.Stall) -> Unit = {}
    ) = SessionWriterMutexGate(
        generation = generation,
        deadlineMs = deadlineMs,
        isCurrentGeneration = { current.get() == generation },
        nowElapsedMs = { System.nanoTime() / 1_000_000 },
        onMutexWaitStall = onStall
    )
}
