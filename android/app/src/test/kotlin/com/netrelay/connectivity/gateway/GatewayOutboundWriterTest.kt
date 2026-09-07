package com.netrelay.connectivity.gateway

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.yield
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Collections
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import kotlin.coroutines.coroutineContext

class GatewayOutboundWriterTest {
    @Test
    fun testA_singleWriter_1000ConcurrentProducersNeverOverlapOutput() = runBlocking {
        val activeWrites = AtomicInteger(0)
        val maxActiveWrites = AtomicInteger(0)
        val harness = harness(write = {
            val active = activeWrites.incrementAndGet()
            updateMax(maxActiveWrites, active)
            delay(1)
            activeWrites.decrementAndGet()
        })
        try {
            val results = List(1_000) { id ->
                async(Dispatchers.Default) { harness.writer.send(pong(id)) }
            }.awaitAll()
            assertTrue(results.all { it })
            assertEquals(1, maxActiveWrites.get())
            assertEquals(1_000, harness.writer.snapshot().dequeuedFrames)
        } finally {
            harness.close()
        }
    }

    @Test
    fun testB_burst150Flows_controlFlowAndDataCompleteWithoutStarvation() = runBlocking {
        val written = Collections.synchronizedList(mutableListOf<Int>())
        val harness = harness(write = { written += it.type }, dataCapacity = 16)
        try {
            val jobs = (1..150).flatMap { flow ->
                listOf(
                    async(Dispatchers.Default) { harness.writer.send(tcpResult(flow)) },
                    async(Dispatchers.Default) { harness.writer.send(tcpData(flow, byteArrayOf(flow.toByte()))) },
                    async(Dispatchers.Default) { harness.writer.send(pong(flow)) }
                )
            }
            assertTrue(jobs.awaitAll().all { it })
            assertEquals(450, written.size)
            assertTrue(written.contains(GatewayProtocol.Type.PONG))
            assertTrue(written.contains(GatewayProtocol.Type.TCP_CONNECT_RESULT))
            assertTrue(written.contains(GatewayProtocol.Type.TCP_DATA_IN))
        } finally {
            harness.close()
        }
    }

    @Test
    fun testC_pongBypassesBulkDataAtNextFrameBoundary() = runBlocking {
        val firstEntered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val order = Collections.synchronizedList(mutableListOf<Int>())
        val writeCount = AtomicInteger(0)
        val harness = harness(write = {
            if (writeCount.getAndIncrement() == 0) {
                firstEntered.complete(Unit)
                release.await()
            }
            order += it.type
        }, dataCapacity = 8)
        try {
            val first = async { harness.writer.send(tcpData(1, byteArrayOf(1))) }
            firstEntered.await()
            val bulk = (2..8).map { id -> async { harness.writer.send(tcpData(id, byteArrayOf(id.toByte()))) } }
            val pong = async { harness.writer.send(pong(99)) }
            delay(20)
            release.complete(Unit)
            assertTrue(pong.await())
            first.await()
            bulk.awaitAll()
            assertEquals(GatewayProtocol.Type.PONG, order[1])
        } finally {
            harness.close()
        }
    }

    @Test
    fun testD_udpOpenResultBypassesDataBacklog() = runBlocking {
        assertPriorityBypassesData(udpResult(77))
    }

    @Test
    fun testE_tcpOpenResultBypassesDataBacklog() = runBlocking {
        assertPriorityBypassesData(tcpResult(77))
    }

    @Test
    fun testF_tcpDataOrderForOneFlowIsByteExact() = runBlocking {
        val bytes = Collections.synchronizedList(mutableListOf<Byte>())
        val harness = harness(write = {
            val decoded = GatewayProtocol.readFrame(it.encoded.inputStream())!!
            if (decoded.type == GatewayProtocol.Type.TCP_DATA_IN) bytes += decoded.payload.toList()
        }, dataCapacity = 8)
        try {
            repeat(5_000) { value ->
                assertTrue(harness.writer.send(tcpData(42, byteArrayOf((value and 0xff).toByte()))))
            }
            assertEquals(5_000, bytes.size)
            repeat(5_000) { value -> assertEquals((value and 0xff).toByte(), bytes[value]) }
        } finally {
            harness.close()
        }
    }

    @Test
    fun testG_flowCloseDoesNotOvertakeAwaitedDataForSameFlow() = runBlocking {
        val order = Collections.synchronizedList(mutableListOf<Int>())
        val firstEntered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val writes = AtomicInteger(0)
        val harness = harness(write = {
            if (writes.getAndIncrement() == 0) {
                firstEntered.complete(Unit)
                release.await()
            }
            order += it.type
        }, dataCapacity = 4)
        try {
            val first = async { harness.writer.send(tcpData(5, byteArrayOf(1))) }
            firstEntered.await()
            val second = async { harness.writer.send(tcpData(5, byteArrayOf(2))) }
            val close = async { harness.writer.send(GatewayProtocol.Frame(GatewayProtocol.Type.FLOW_CLOSED, 5)) }
            delay(20)
            release.complete(Unit)
            assertTrue(first.await())
            assertTrue(second.await())
            assertTrue(close.await())
            assertEquals(GatewayProtocol.Type.FLOW_CLOSED, order.last())
            assertEquals(2, order.dropLast(1).count { it == GatewayProtocol.Type.TCP_DATA_IN })
        } finally {
            harness.close()
        }
    }

    @Test
    fun testH_slowConsumerNeverExceedsBoundedDepth() = runBlocking {
        val harness = harness(write = { delay(2) }, dataCapacity = 4)
        try {
            val jobs = List(200) { id -> async(Dispatchers.Default) { harness.writer.send(tcpData(id, ByteArray(1024))) } }
            jobs.awaitAll()
            val snapshot = harness.writer.snapshot()
            assertTrue(snapshot.maxDataDepth <= 4)
            assertTrue(snapshot.tcpBackpressureCount > 0)
        } finally {
            harness.close()
        }
    }

    @Test
    fun testI_fullTcpQueueSuspendsThenResumesWithoutSessionAbort() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val calls = AtomicInteger(0)
        val harness = harness(write = {
            if (calls.getAndIncrement() == 0) {
                entered.complete(Unit)
                release.await()
            }
        }, dataCapacity = 1)
        try {
            val first = async { harness.writer.send(tcpData(1, byteArrayOf(1))) }
            entered.await()
            val queued = async { harness.writer.send(tcpData(2, byteArrayOf(2))) }
            val blocked = async { harness.writer.send(tcpData(3, byteArrayOf(3))) }
            delay(50)
            assertFalse(blocked.isCompleted)
            release.complete(Unit)
            assertTrue(first.await())
            assertTrue(queued.await())
            assertTrue(blocked.await())
            assertTrue(harness.writer.snapshot().tcpBackpressureCount > 0)
            assertTrue(harness.current.get())
        } finally {
            harness.close()
        }
    }

    @Test
    fun testJ_udpCongestionDropsPayloadOnlyWithoutUnboundedGrowthOrAbort() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val calls = AtomicInteger(0)
        val harness = harness(write = {
            if (calls.getAndIncrement() == 0) {
                entered.complete(Unit)
                release.await()
            }
        }, dataCapacity = 2)
        try {
            val first = async { harness.writer.send(tcpData(1, byteArrayOf(1))) }
            entered.await()
            val udp = (1..100).map { id -> async { harness.writer.send(udpData(id)) } }
            delay(50)
            assertTrue(harness.writer.snapshot().udpDroppedCount > 0)
            assertTrue(harness.writer.snapshot().maxDataDepth <= 2)
            assertTrue(harness.current.get())
            release.complete(Unit)
            first.await()
            udp.awaitAll()
            Unit
        } finally {
            harness.close()
        }
    }

    @Test
    fun testK_stalledOutputIsReleasedByExternalTransportWatchdogCancellation() = runBlocking {
        val entered = CompletableDeferred<Unit>()
        val never = CompletableDeferred<Unit>()
        val harness = harness(write = {
            entered.complete(Unit)
            never.await()
        })
        val send = async { harness.writer.send(tcpData(1, byteArrayOf(1))) }
        entered.await()
        harness.writer.invalidate()
        assertFalse(withTimeout(500) { send.await() })
        harness.close()
    }

    @Test
    fun testL_generationResetDiscardsOldBacklogAndFreshWriterStartsImmediately() = runBlocking {
        val oldEntered = CompletableDeferred<Unit>()
        val oldNever = CompletableDeferred<Unit>()
        val old = harness(generation = 1, write = {
            oldEntered.complete(Unit)
            oldNever.await()
        }, dataCapacity = 2)
        val oldSend = async { old.writer.send(tcpData(1, byteArrayOf(1))) }
        oldEntered.await()
        val oldQueued = async { old.writer.send(tcpData(2, byteArrayOf(2))) }
        old.current.set(false)
        old.writer.invalidate()
        assertFalse(withTimeout(500) { oldSend.await() })
        assertFalse(withTimeout(500) { oldQueued.await() })

        val freshWrites = AtomicInteger(0)
        val fresh = harness(generation = 2, write = { freshWrites.incrementAndGet() })
        try {
            assertTrue(withTimeout(500) { fresh.writer.send(pong(1)) })
            assertEquals(1, freshWrites.get())
        } finally {
            old.close()
            fresh.close()
        }
    }

    @Test
    fun testM_tenReconnectCyclesKeepWritersSessionLocal() = runBlocking {
        repeat(10) { generation ->
            val writes = AtomicInteger(0)
            val harness = harness(generation = generation.toLong() + 1, write = { writes.incrementAndGet() })
            assertTrue(harness.writer.send(pong(generation)))
            assertEquals(1, writes.get())
            harness.close()
        }
    }

    @Test
    fun testO_ael2FrameIntegrityForAllOutboundTypes() = runBlocking {
        val types = listOf(
            GatewayProtocol.Type.HELLO_ACK, GatewayProtocol.Type.PONG,
            GatewayProtocol.Type.TCP_CONNECT_RESULT, GatewayProtocol.Type.UDP_OPEN_RESULT,
            GatewayProtocol.Type.FLOW_CLOSED, GatewayProtocol.Type.ERROR,
            GatewayProtocol.Type.TCP_DATA_IN, GatewayProtocol.Type.UDP_DATAGRAM_IN
        )
        val decoded = Collections.synchronizedList(mutableListOf<GatewayProtocol.Frame>())
        val harness = harness(write = { decoded += GatewayProtocol.readFrame(it.encoded.inputStream())!! })
        try {
            types.forEachIndexed { index, type ->
                val payload = if (type == GatewayProtocol.Type.UDP_DATAGRAM_IN) {
                    GatewayProtocol.encodeDatagram("1.1.1.1", 53, byteArrayOf(1, 2, 3))
                } else byteArrayOf(index.toByte())
                assertTrue(harness.writer.send(GatewayProtocol.Frame(type, index, index, payload = payload)))
            }
            assertEquals(types, decoded.map { it.type })
            decoded.forEachIndexed { index, frame -> assertEquals(index, frame.flowId) }
        } finally {
            harness.close()
        }
    }

    @Test
    fun testP_priorityClassificationKeepsPongAndOpenResultsOutOfBulkData() {
        assertEquals(GatewayOutboundWriter.Priority.CONTROL, GatewayOutboundWriter.priorityFor(GatewayProtocol.Type.PONG))
        assertEquals(GatewayOutboundWriter.Priority.FLOW_CONTROL, GatewayOutboundWriter.priorityFor(GatewayProtocol.Type.TCP_CONNECT_RESULT))
        assertEquals(GatewayOutboundWriter.Priority.FLOW_CONTROL, GatewayOutboundWriter.priorityFor(GatewayProtocol.Type.UDP_OPEN_RESULT))
        assertEquals(GatewayOutboundWriter.Priority.DATA, GatewayOutboundWriter.priorityFor(GatewayProtocol.Type.TCP_DATA_IN))
        assertEquals(GatewayOutboundWriter.Priority.DATA, GatewayOutboundWriter.priorityFor(GatewayProtocol.Type.UDP_DATAGRAM_IN))
        assertEquals(GatewayOutboundWriter.Priority.DATA, GatewayOutboundWriter.priorityFor(GatewayProtocol.Type.FLOW_CLOSED))
    }

    @Test
    fun testQ_writerHasNoUsbWifiVpnOrWintunDependencies() {
        val dependencies = GatewayOutboundWriter::class.java.declaredFields.joinToString(" ") { it.type.name }
        assertFalse(dependencies.contains("usb", ignoreCase = true))
        assertFalse(dependencies.contains("wifi", ignoreCase = true))
        assertFalse(dependencies.contains("vpn", ignoreCase = true))
        assertFalse(dependencies.contains("wintun", ignoreCase = true))
    }

    @Test
    fun stress200Flows_mixedFramesSlowConsumer_noDeadlockNoCorruptionNoStarvation() = runBlocking {
        val startedNs = System.nanoTime()
        val written = Collections.synchronizedList(mutableListOf<GatewayOutboundWriter.QueuedFrame>())
        val concurrent = AtomicInteger(0)
        val maxConcurrent = AtomicInteger(0)
        val harness = harness(write = {
            updateMax(maxConcurrent, concurrent.incrementAndGet())
            yield()
            written += it
            concurrent.decrementAndGet()
        }, dataCapacity = 16)
        try {
            val jobs = (1..200).flatMap { flow ->
                listOf(
                    async(Dispatchers.Default) { harness.writer.send(tcpResult(flow)) },
                    async(Dispatchers.Default) { harness.writer.send(tcpData(flow, ByteArray(512) { flow.toByte() })) },
                    async(Dispatchers.Default) { harness.writer.send(udpResult(flow + 1_000)) },
                    async(Dispatchers.Default) { harness.writer.send(udpData(flow + 1_000)) },
                    async(Dispatchers.Default) { harness.writer.send(pong(flow)) },
                    async(Dispatchers.Default) { harness.writer.send(GatewayProtocol.Frame(GatewayProtocol.Type.FLOW_CLOSED, flow)) }
                )
            }
            withTimeout(60_000) { jobs.awaitAll() }
            val snapshot = harness.writer.snapshot()
            assertEquals(1, maxConcurrent.get())
            assertTrue(snapshot.maxDataDepth <= 16)
            assertTrue(snapshot.maxControlDepth <= GatewayOutboundWriter.CONTROL_CAPACITY)
            assertTrue(snapshot.maxFlowControlDepth <= GatewayOutboundWriter.FLOW_CONTROL_CAPACITY)
            assertTrue(snapshot.maxPongLatencyMs < 10_000)
            assertTrue(snapshot.maxOpenResultLatencyMs < 10_000)
            written.forEach { GatewayProtocol.readFrame(it.encoded.inputStream())!! }
            assertTrue(harness.current.get())
            val elapsedMs = ((System.nanoTime() - startedNs) / 1_000_000).coerceAtLeast(1)
            val tcpBytes = written.filter { it.type == GatewayProtocol.Type.TCP_DATA_IN }
                .sumOf { GatewayProtocol.readFrame(it.encoded.inputStream())!!.payload.size.toLong() }
            val tcpBytesPerSecond = tcpBytes * 1_000 / elapsedMs
            println(
                "OUTQ_STRESS_METRICS " +
                    "maxControlAgeMs=${snapshot.maxControlAgeMs} " +
                    "maxFlowControlAgeMs=${snapshot.maxFlowControlAgeMs} " +
                    "maxDataAgeMs=${snapshot.maxDataAgeMs} " +
                    "maxControlDepth=${snapshot.maxControlDepth} " +
                    "maxFlowControlDepth=${snapshot.maxFlowControlDepth} " +
                    "maxDataDepth=${snapshot.maxDataDepth} " +
                    "maxPongLatencyMs=${snapshot.maxPongLatencyMs} " +
                    "maxOpenResultLatencyMs=${snapshot.maxOpenResultLatencyMs} " +
                    "tcpBytesPerSecond=$tcpBytesPerSecond udpDropped=${snapshot.udpDroppedCount}"
            )
        } finally {
            harness.close()
        }
    }

    private suspend fun assertPriorityBypassesData(priorityFrame: GatewayProtocol.Frame) {
        val entered = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val order = Collections.synchronizedList(mutableListOf<Int>())
        val calls = AtomicInteger(0)
        val harness = harness(write = {
            if (calls.getAndIncrement() == 0) {
                entered.complete(Unit)
                release.await()
            }
            order += it.type
        }, dataCapacity = 8)
        try {
            val first = CoroutineScope(coroutineContext).async { harness.writer.send(tcpData(1, byteArrayOf(1))) }
            entered.await()
            val bulk = (2..8).map { id -> CoroutineScope(coroutineContext).async { harness.writer.send(tcpData(id, byteArrayOf(id.toByte()))) } }
            val priority = CoroutineScope(coroutineContext).async { harness.writer.send(priorityFrame) }
            delay(20)
            release.complete(Unit)
            assertTrue(priority.await())
            first.await()
            bulk.awaitAll()
            assertEquals(priorityFrame.type, order[1])
        } finally {
            harness.close()
        }
    }

    private data class Harness(
        val writer: GatewayOutboundWriter,
        val scope: CoroutineScope,
        val current: AtomicBoolean
    ) {
        fun close() {
            current.set(false)
            writer.invalidate()
            scope.cancel()
        }
    }

    private fun harness(
        generation: Long = 1,
        write: suspend (GatewayOutboundWriter.QueuedFrame) -> Unit,
        controlCapacity: Int = GatewayOutboundWriter.CONTROL_CAPACITY,
        flowCapacity: Int = GatewayOutboundWriter.FLOW_CONTROL_CAPACITY,
        dataCapacity: Int = GatewayOutboundWriter.DATA_CAPACITY
    ): Harness {
        val current = AtomicBoolean(true)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
        val writer = GatewayOutboundWriter(
            sessionGeneration = generation,
            scope = scope,
            isCurrentGeneration = { current.get() },
            writeEncoded = write,
            nowElapsedMs = { System.nanoTime() / 1_000_000 },
            controlCapacity = controlCapacity,
            flowControlCapacity = flowCapacity,
            dataCapacity = dataCapacity
        )
        return Harness(writer, scope, current)
    }

    private fun pong(sequence: Int) = GatewayProtocol.Frame(GatewayProtocol.Type.PONG, arg = sequence)
    private fun tcpResult(flow: Int) = GatewayProtocol.Frame(GatewayProtocol.Type.TCP_CONNECT_RESULT, flow)
    private fun udpResult(flow: Int) = GatewayProtocol.Frame(GatewayProtocol.Type.UDP_OPEN_RESULT, flow)
    private fun tcpData(flow: Int, payload: ByteArray) = GatewayProtocol.Frame(GatewayProtocol.Type.TCP_DATA_IN, flow, payload = payload)
    private fun udpData(flow: Int) = GatewayProtocol.Frame(
        GatewayProtocol.Type.UDP_DATAGRAM_IN,
        flow,
        payload = GatewayProtocol.encodeDatagram("1.1.1.1", 53, byteArrayOf(1, 2, 3))
    )

    private fun updateMax(target: AtomicInteger, value: Int) {
        var current = target.get()
        while (value > current && !target.compareAndSet(current, value)) current = target.get()
    }
}
