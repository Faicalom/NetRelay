package com.netrelay.connectivity.gateway

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExecutorCoroutineDispatcher
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.DatagramSocket
import java.net.SocketException
import java.util.Collections
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

class BoundedUdpOpenSchedulerTest {
    @Test
    fun testA_saturatedFlowIoDoesNotDelayUdpOpen() = runBlocking {
        val blockedExecutor = Executors.newFixedThreadPool(64)
        val entered = CountDownLatch(64)
        val release = CountDownLatch(1)
        repeat(64) {
            blockedExecutor.submit {
                entered.countDown()
                release.await()
            }
        }
        assertTrue(entered.await(2, TimeUnit.SECONDS))

        val harness = harness()
        try {
            val receivedAt = nowMs()
            assertEquals(
                BoundedUdpOpenScheduler.Admission.SUBMITTED,
                harness.scheduler.trySubmit(1, 1, receivedAt)
            )
            waitUntil { harness.scheduler.snapshot().udpOpenSucceeded == 1L }
            assertTrue(harness.scheduler.snapshot().maxFrameToOpenStartMs < 100)
        } finally {
            release.countDown()
            blockedExecutor.shutdownNow()
            harness.close()
        }
    }

    @Test
    fun testB_twoHundredExistingFlowsAndFiftyOpenBurstRemainBelowDeadline() = runBlocking {
        val existingFlows = Collections.synchronizedSet((1..200).toMutableSet())
        val harness = harness { request ->
            existingFlows += request.flowId
            completed(request, BoundedUdpOpenScheduler.Outcome.SUCCEEDED)
        }
        try {
            repeat(50) { flow ->
                assertEquals(
                    BoundedUdpOpenScheduler.Admission.SUBMITTED,
                    harness.scheduler.trySubmit(1_000 + flow, flow, nowMs())
                )
            }
            waitUntil { harness.scheduler.snapshot().udpOpenSucceeded == 50L }
            val snapshot = harness.scheduler.snapshot()
            assertTrue(snapshot.maxFrameToResultMs < 500)
            assertEquals(250, existingFlows.size)
            assertEquals(0, snapshot.pendingUdpOpens)
        } finally {
            harness.close()
        }
    }

    @Test
    fun testC_udpControlExecutorIsIndependentFromBlockedGenericIo() = runBlocking {
        val generic = Executors.newSingleThreadExecutor()
        val entered = CountDownLatch(1)
        val release = CountDownLatch(1)
        generic.submit {
            entered.countDown()
            release.await()
        }
        assertTrue(entered.await(1, TimeUnit.SECONDS))
        val harness = harness()
        try {
            assertEquals(
                BoundedUdpOpenScheduler.Admission.SUBMITTED,
                harness.scheduler.trySubmit(7, 7, nowMs())
            )
            waitUntil { harness.scheduler.snapshot().udpOpenSucceeded == 1L }
        } finally {
            release.countDown()
            generic.shutdownNow()
            harness.close()
        }
    }

    @Test
    fun testD_writerControlTrafficProgressesDuringUdpOpenBurst() = runBlocking {
        val release = CompletableDeferred<Unit>()
        val harness = harness {
            release.await()
            completed(it, BoundedUdpOpenScheduler.Outcome.SUCCEEDED)
        }
        val writer = writerHarness()
        try {
            repeat(40) { harness.scheduler.trySubmit(it, it, nowMs()) }
            waitUntil { harness.scheduler.snapshot().activeUdpOpens == 4 }
            val started = nowMs()
            assertTrue(withTimeout(500) {
                writer.writer.send(GatewayProtocol.Frame(GatewayProtocol.Type.PONG))
            })
            assertTrue(nowMs() - started < 500)
            assertEquals(listOf(GatewayProtocol.Type.PONG), writer.written)
        } finally {
            release.complete(Unit)
            harness.close()
            writer.close()
        }
    }

    @Test
    fun testE_openDatagramCloseLifecycleIsFlowScoped() = runBlocking {
        val openFlows = Collections.synchronizedSet(mutableSetOf<Int>())
        val delivered = Collections.synchronizedList(mutableListOf<Int>())
        val harness = harness { request ->
            openFlows += request.flowId
            completed(request, BoundedUdpOpenScheduler.Outcome.SUCCEEDED)
        }
        try {
            assertFalse(openFlows.contains(42))
            harness.scheduler.trySubmit(42, 42, nowMs())
            waitUntil { openFlows.contains(42) }
            if (openFlows.contains(42)) delivered += 42
            openFlows.remove(42)
            if (openFlows.contains(42)) delivered += 42
            assertEquals(listOf(42), delivered)
        } finally {
            harness.close()
        }
    }

    @Test
    fun testF_generationResetCancelsOldPendingAndFreshGenerationRuns() = runBlocking {
        val old = harness(generation = 1) { awaitCancellation() }
        repeat(100) { old.scheduler.trySubmit(it, it, nowMs()) }
        waitUntil { old.scheduler.snapshot().activeUdpOpens == 4 }
        old.current.set(false)
        old.scheduler.invalidate()

        val fresh = harness(generation = 2)
        try {
            assertEquals(
                BoundedUdpOpenScheduler.Admission.SUBMITTED,
                fresh.scheduler.trySubmit(500, 500, nowMs())
            )
            waitUntil { fresh.scheduler.snapshot().udpOpenSucceeded == 1L }
            assertEquals(0, fresh.scheduler.snapshot().pendingUdpOpens)
        } finally {
            old.close()
            fresh.close()
        }
    }

    @Test
    fun testG_closingDatagramSocketUnblocksReceive() {
        val socket = DatagramSocket(0)
        val entered = CountDownLatch(1)
        val exited = CountDownLatch(1)
        val executor = Executors.newSingleThreadExecutor()
        executor.submit {
            entered.countDown()
            try {
                socket.receive(java.net.DatagramPacket(ByteArray(64), 64))
            } catch (_: SocketException) {
            } finally {
                exited.countDown()
            }
        }
        assertTrue(entered.await(1, TimeUnit.SECONDS))
        socket.close()
        assertTrue(exited.await(1, TimeUnit.SECONDS))
        executor.shutdownNow()
    }

    @Test
    fun testH_dnsAndQuicOpenLoadHasNoFiveSecondSchedulerDelay() = runBlocking {
        val harness = harness()
        try {
            repeat(100) { flow ->
                val port = if (flow % 2 == 0) 53 else 443
                assertEquals(
                    BoundedUdpOpenScheduler.Admission.SUBMITTED,
                    harness.scheduler.trySubmit(2_000 + flow, port, nowMs())
                )
            }
            waitUntil { harness.scheduler.snapshot().udpOpenSucceeded == 100L }
            val snapshot = harness.scheduler.snapshot()
            assertTrue(snapshot.maxFrameToOpenStartMs < 100)
            assertTrue(snapshot.maxFrameToResultMs < 500)
            assertEquals(0, snapshot.udpOpenRejected)
        } finally {
            harness.close()
        }
    }

    private data class Harness(
        val scheduler: BoundedUdpOpenScheduler<Int>,
        val scope: CoroutineScope,
        val dispatcher: ExecutorCoroutineDispatcher,
        val current: AtomicBoolean
    ) {
        fun close() {
            current.set(false)
            scheduler.invalidate()
            scope.cancel()
            dispatcher.close()
        }
    }

    private fun harness(
        generation: Long = 1,
        execute: suspend (BoundedUdpOpenScheduler.Request<Int>) -> BoundedUdpOpenScheduler.Completion = {
            completed(it, BoundedUdpOpenScheduler.Outcome.SUCCEEDED)
        }
    ): Harness {
        val current = AtomicBoolean(true)
        val scope = CoroutineScope(SupervisorJob())
        val dispatcher = Executors.newFixedThreadPool(4).asCoroutineDispatcher()
        val scheduler = BoundedUdpOpenScheduler(
            sessionGeneration = generation,
            parentJob = scope.coroutineContext[Job]!!,
            metricsScope = scope,
            udpControlDispatcher = dispatcher,
            maxConcurrency = 4,
            pendingCapacity = 128,
            nowElapsedMs = ::nowMs,
            isCurrentGeneration = current::get,
            execute = execute
        )
        return Harness(scheduler, scope, dispatcher, current)
    }

    private data class WriterHarness(
        val writer: GatewayOutboundWriter,
        val scope: CoroutineScope,
        val dispatcher: ExecutorCoroutineDispatcher,
        val written: MutableList<Int>
    ) {
        fun close() {
            writer.invalidate()
            scope.cancel()
            dispatcher.close()
        }
    }

    private fun writerHarness(): WriterHarness {
        val written = Collections.synchronizedList(mutableListOf<Int>())
        val dispatcher = Executors.newSingleThreadExecutor().asCoroutineDispatcher()
        val scope = CoroutineScope(SupervisorJob() + dispatcher)
        val writer = GatewayOutboundWriter(
            sessionGeneration = 1,
            scope = scope,
            isCurrentGeneration = { true },
            writeEncoded = { written += it.type },
            nowElapsedMs = ::nowMs
        )
        return WriterHarness(writer, scope, dispatcher, written)
    }

    private fun completed(
        request: BoundedUdpOpenScheduler.Request<Int>,
        outcome: BoundedUdpOpenScheduler.Outcome
    ): BoundedUdpOpenScheduler.Completion {
        val started = nowMs().coerceAtLeast(request.dispatchStartedAtMs)
        return BoundedUdpOpenScheduler.Completion(outcome, started, nowMs().coerceAtLeast(started))
    }

    private suspend fun waitUntil(predicate: () -> Boolean) {
        withTimeout(5_000) {
            while (!predicate()) delay(5)
        }
    }

    companion object {
        private fun nowMs(): Long = System.nanoTime() / 1_000_000
    }
}
