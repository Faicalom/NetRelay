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
import java.util.Collections
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

class BoundedConnectSchedulerTest {
    @Test
    fun testA_200ConnectBurstNeverExceedsConfiguredConcurrency() = runBlocking {
        val release = CompletableDeferred<Unit>()
        val harness = harness(maxConcurrency = 16, pendingCapacity = 64) {
            release.await()
            BoundedConnectScheduler.Outcome.SUCCEEDED
        }
        try {
            repeat(200) { harness.scheduler.trySubmit(it, it) }
            waitUntil { harness.scheduler.snapshot().activeTcpConnects == 16 }
            val saturated = harness.scheduler.snapshot()
            assertEquals(16, saturated.maxConcurrentTcpConnects)
            assertTrue(saturated.pendingTcpConnects <= 64)
            assertTrue(saturated.connectRejected >= 120)
            println(
                "CONNECT_BURST_METRICS maxActive=${saturated.maxConcurrentTcpConnects} " +
                    "maxPending=${saturated.maxPendingTcpConnects} rejected=${saturated.connectRejected}"
            )
            release.complete(Unit)
            waitUntil { harness.scheduler.snapshot().activeTcpConnects == 0 }
        } finally {
            harness.close()
        }
    }

    @Test
    fun testB_writerPongSurvivesSaturatedConnectWorkers() = runBlocking {
        val release = CompletableDeferred<Unit>()
        val harness = harness(maxConcurrency = 4, pendingCapacity = 8) {
            release.await()
            BoundedConnectScheduler.Outcome.SUCCEEDED
        }
        val writer = writerHarness()
        try {
            repeat(4) { assertTrue(harness.scheduler.trySubmit(it, it)) }
            waitUntil { harness.scheduler.snapshot().activeTcpConnects == 4 }
            val started = System.nanoTime()
            assertTrue(withTimeout(500) {
                writer.writer.send(GatewayProtocol.Frame(GatewayProtocol.Type.PONG, arg = 7))
            })
            val latencyMs = (System.nanoTime() - started) / 1_000_000
            assertTrue("PONG latency=$latencyMs", latencyMs < 500)
            println("CONNECT_ISOLATION_PONG_LATENCY_MS=$latencyMs")
        } finally {
            release.complete(Unit)
            harness.close()
            writer.close()
        }
    }

    @Test
    fun testC_openResultProgressesWhileOtherConnectsAreBlocked() = runBlocking {
        val release = CompletableDeferred<Unit>()
        lateinit var writer: WriterHarness
        val completed = CompletableDeferred<Unit>()
        val harness = harness(maxConcurrency = 16, pendingCapacity = 16) { request ->
            if (request.flowId == 16) {
                writer.writer.send(GatewayProtocol.Frame(GatewayProtocol.Type.TCP_CONNECT_RESULT, 16))
                completed.complete(Unit)
                BoundedConnectScheduler.Outcome.SUCCEEDED
            } else {
                release.await()
                BoundedConnectScheduler.Outcome.SUCCEEDED
            }
        }
        writer = writerHarness()
        try {
            repeat(16) { assertTrue(harness.scheduler.trySubmit(it + 1, it + 1)) }
            val started = System.nanoTime()
            withTimeout(500) { completed.await() }
            val latencyMs = (System.nanoTime() - started) / 1_000_000
            assertTrue(writer.written.contains(GatewayProtocol.Type.TCP_CONNECT_RESULT))
            println("CONNECT_SATURATION_OPEN_RESULT_LATENCY_MS=$latencyMs")
        } finally {
            release.complete(Unit)
            harness.close()
            writer.close()
        }
    }

    @Test
    fun testD_pendingAdmissionIsBoundedAndExcessIsFlowScoped() = runBlocking {
        val release = CompletableDeferred<Unit>()
        val harness = harness(maxConcurrency = 8, pendingCapacity = 32) {
            release.await()
            BoundedConnectScheduler.Outcome.SUCCEEDED
        }
        try {
            repeat(2_000) { harness.scheduler.trySubmit(it, it) }
            waitUntil { harness.scheduler.snapshot().activeTcpConnects == 8 }
            val snapshot = harness.scheduler.snapshot()
            assertTrue(snapshot.activeTcpConnects <= 8)
            assertTrue(snapshot.pendingTcpConnects <= 32)
            assertTrue(snapshot.maxPendingTcpConnects <= 32)
            assertTrue(snapshot.connectRejected > 0)
            assertTrue(harness.current.get())
        } finally {
            release.complete(Unit)
            harness.close()
        }
    }

    @Test
    fun testE_connectTimeoutIsFlowScopedAndSchedulerStaysAlive() = runBlocking {
        val harness = harness(maxConcurrency = 2, pendingCapacity = 4) { request ->
            if (request.flowId == 1) BoundedConnectScheduler.Outcome.TIMED_OUT
            else BoundedConnectScheduler.Outcome.SUCCEEDED
        }
        try {
            assertTrue(harness.scheduler.trySubmit(1, 1))
            assertTrue(harness.scheduler.trySubmit(2, 2))
            waitUntil {
                val snapshot = harness.scheduler.snapshot()
                snapshot.connectTimedOut == 1L && snapshot.connectSucceeded == 1L
            }
            assertTrue(harness.current.get())
            assertEquals(0, harness.scheduler.snapshot().activeTcpConnects)
        } finally {
            harness.close()
        }
    }

    @Test
    fun testF_generationResetCancelsOldPendingAndFreshGenerationRuns() = runBlocking {
        val old = harness(generation = 1, maxConcurrency = 4, pendingCapacity = 8) {
            awaitCancellation()
        }
        repeat(12) { old.scheduler.trySubmit(it, it) }
        waitUntil { old.scheduler.snapshot().activeTcpConnects == 4 }
        old.current.set(false)
        old.scheduler.invalidate()

        val fresh = harness(generation = 2, maxConcurrency = 4, pendingCapacity = 8) {
            BoundedConnectScheduler.Outcome.SUCCEEDED
        }
        try {
            assertTrue(fresh.scheduler.trySubmit(100, 100))
            waitUntil { fresh.scheduler.snapshot().connectSucceeded == 1L }
            assertEquals(0, fresh.scheduler.snapshot().pendingTcpConnects)
        } finally {
            old.close()
            fresh.close()
        }
    }

    @Test
    fun testG_connectFloodAndMixedWriterTrafficDoNotDeadlock() = runBlocking {
        val release = CompletableDeferred<Unit>()
        val harness = harness(maxConcurrency = 8, pendingCapacity = 32) {
            release.await()
            BoundedConnectScheduler.Outcome.SUCCEEDED
        }
        val writer = writerHarness()
        try {
            repeat(200) { harness.scheduler.trySubmit(it, it) }
            repeat(100) { flow ->
                assertTrue(writer.writer.send(GatewayProtocol.Frame(GatewayProtocol.Type.PONG, arg = flow)))
                assertTrue(writer.writer.send(GatewayProtocol.Frame(GatewayProtocol.Type.ERROR, flow)))
                assertTrue(writer.writer.send(GatewayProtocol.Frame(GatewayProtocol.Type.FLOW_CLOSED, flow)))
            }
            assertEquals(300, writer.written.size)
            assertTrue(harness.scheduler.snapshot().pendingTcpConnects <= 32)
        } finally {
            release.complete(Unit)
            harness.close()
            writer.close()
        }
    }

    @Test
    fun testH_connectDispatcherThreadsCanBlockWithoutBlockingWriterDispatcher() = runBlocking {
        val connectExecutor = Executors.newFixedThreadPool(4)
        val connectDispatcher = connectExecutor.asCoroutineDispatcher()
        val entered = CountDownLatch(4)
        val release = CountDownLatch(1)
        repeat(4) {
            connectExecutor.submit {
                entered.countDown()
                release.await()
            }
        }
        assertTrue(entered.await(2, TimeUnit.SECONDS))
        val writer = writerHarness()
        try {
            assertTrue(withTimeout(500) {
                writer.writer.send(GatewayProtocol.Frame(GatewayProtocol.Type.PONG))
            })
        } finally {
            release.countDown()
            writer.close()
            connectDispatcher.close()
        }
    }

    @Test
    fun testI_flowIoThreadsCanBlockWithoutBlockingControlOrWriter() = runBlocking {
        val flowExecutor = Executors.newFixedThreadPool(8)
        val flowDispatcher = flowExecutor.asCoroutineDispatcher()
        val entered = CountDownLatch(8)
        val release = CountDownLatch(1)
        repeat(8) {
            flowExecutor.submit {
                entered.countDown()
                release.await()
            }
        }
        assertTrue(entered.await(2, TimeUnit.SECONDS))
        val writer = writerHarness()
        try {
            assertTrue(withTimeout(500) {
                writer.writer.send(GatewayProtocol.Frame(GatewayProtocol.Type.PONG))
            })
        } finally {
            release.countDown()
            writer.close()
            flowDispatcher.close()
        }
    }

    private data class SchedulerHarness(
        val scheduler: BoundedConnectScheduler<Int>,
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
        maxConcurrency: Int,
        pendingCapacity: Int,
        execute: suspend (BoundedConnectScheduler.Request<Int>) -> BoundedConnectScheduler.Outcome
    ): SchedulerHarness {
        val current = AtomicBoolean(true)
        val scope = CoroutineScope(SupervisorJob())
        val dispatcher = Executors.newFixedThreadPool(maxConcurrency).asCoroutineDispatcher()
        val parentJob = scope.coroutineContext[Job]!!
        val scheduler = BoundedConnectScheduler(
            sessionGeneration = generation,
            parentJob = parentJob,
            metricsScope = scope,
            connectDispatcher = dispatcher,
            maxConcurrency = maxConcurrency,
            pendingCapacity = pendingCapacity,
            nowElapsedMs = { System.nanoTime() / 1_000_000 },
            isCurrentGeneration = { current.get() },
            execute = execute
        )
        return SchedulerHarness(scheduler, scope, dispatcher, current)
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
            nowElapsedMs = { System.nanoTime() / 1_000_000 }
        )
        return WriterHarness(writer, scope, dispatcher, written)
    }

    private suspend fun waitUntil(predicate: () -> Boolean) {
        withTimeout(5_000) {
            while (!predicate()) delay(5)
        }
    }
}
