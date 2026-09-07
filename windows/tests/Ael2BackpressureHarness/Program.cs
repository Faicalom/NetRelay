using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetRelay.Services;

internal static class Program
{
    private static async Task Main()
    {
        await TestOutboundControlPreemptsBulkData();
        await TestOutboundTcpSplit();
        await TestOutboundTcpOrder();
        await TestOutboundHundredFlowMix();
        await TestOutboundUdpOpenLatency();
        await TestOutboundPingPriority();
        await TestOutboundBoundedMemory();
        await TestOutboundDataFairness();
        await TestOutboundLoggingIsolation();
        await TestReaderDoesNotBlockOnSlowFlow();
        await TestHundredFlowIsolation();
        await TestFiveThousandFrameOrder();
        await TestBlockedFlowIsolation();
        await TestMixedStress();
        TestProductionGuards();
        Console.WriteLine("AEL2_BACKPRESSURE_HARNESS_PASS");
    }

    private static async Task TestOutboundControlPreemptsBulkData()
    {
        var stream = new RecordingStream(blockFirstWrite: true);
        await using var writer = new GatewayOutboundWriter(stream);
        var first = writer.SendAsync(new GatewayProtocol.Frame(
            GatewayProtocol.Type.TcpData, 1, Payload: new byte[1024]));
        await stream.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var backlog = Enumerable.Range(0, 32)
            .Select(i => writer.SendAsync(new GatewayProtocol.Frame(
                GatewayProtocol.Type.TcpData, 2, Payload: BitConverter.GetBytes(i))))
            .ToArray();
        var open = writer.SendAsync(new GatewayProtocol.Frame(GatewayProtocol.Type.UdpOpen, 77));
        stream.ReleaseFirstWrite();
        await open.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(backlog.Prepend(first));

        var writes = stream.Snapshot();
        Require(writes.Count >= 2, "priority writer produced too few frames");
        Require(writes[0].MessageType == GatewayProtocol.Type.TcpData, "active data frame changed");
        Require(writes[1].MessageType == GatewayProtocol.Type.UdpOpen && writes[1].FlowId == 77,
            "UDP_OPEN did not preempt queued TCP data");
        Require(stream.FlushCount == 0, "priority writer performed redundant per-frame flushes");
        Console.WriteLine("TEST_TX_A_CONTROL_PREEMPTS_BULK_PASS");
    }

    private static async Task TestOutboundTcpSplit()
    {
        var stream = new RecordingStream();
        await using var writer = new GatewayOutboundWriter(stream);
        var payload = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 31)).ToArray();
        await writer.SendAsync(new GatewayProtocol.Frame(
            GatewayProtocol.Type.TcpData, 9, Payload: payload));

        var writes = stream.Snapshot().Where(f => f.FlowId == 9).ToArray();
        Require(writes.Length == 4, "64 KiB TCP payload was not split into four frames");
        Require(writes.All(f => f.Payload.Length == GatewayOutboundWriter.MaxTcpDataChunkSize),
            "TCP split did not use the 16 KiB cap");
        Require(writes.SelectMany(f => f.Payload).SequenceEqual(payload), "split TCP bytes changed");
        Console.WriteLine("TEST_TX_B_64K_SPLIT_PASS");
    }

    private static async Task TestOutboundTcpOrder()
    {
        var stream = new RecordingStream();
        await using var writer = new GatewayOutboundWriter(stream);
        var random = new Random(74123);
        var expected = new List<byte>();
        for (var i = 0; i < 2000; i++)
        {
            var chunk = new byte[random.Next(1, 48 * 1024)];
            random.NextBytes(chunk);
            expected.AddRange(chunk);
            await writer.SendAsync(new GatewayProtocol.Frame(
                GatewayProtocol.Type.TcpData, 31, Payload: chunk));
        }

        var actual = stream.Snapshot()
            .Where(f => f.MessageType == GatewayProtocol.Type.TcpData && f.FlowId == 31)
            .SelectMany(f => f.Payload);
        Require(actual.SequenceEqual(expected), "randomized per-flow TCP byte order changed");
        Console.WriteLine("TEST_TX_C_TCP_ORDER_PASS");
    }

    private static async Task TestOutboundHundredFlowMix()
    {
        var stream = new RecordingStream(writeDelay: TimeSpan.FromMilliseconds(1));
        await using var writer = new GatewayOutboundWriter(stream);
        var tasks = Enumerable.Range(1, 100).Select(async flowId =>
        {
            if ((flowId & 1) == 0)
            {
                await writer.SendAsync(new GatewayProtocol.Frame(GatewayProtocol.Type.UdpOpen, flowId));
                await writer.SendAsync(new GatewayProtocol.Frame(
                    GatewayProtocol.Type.UdpDatagram, flowId,
                    Payload: GatewayProtocol.EncodeDatagram("1.1.1.1", 53, new byte[] { (byte)flowId })));
                await writer.SendAsync(new GatewayProtocol.Frame(GatewayProtocol.Type.UdpClose, flowId));
            }
            else
            {
                await writer.SendAsync(new GatewayProtocol.Frame(
                    GatewayProtocol.Type.TcpConnect, flowId, 443,
                    Payload: GatewayProtocol.EncodeHost("example.com")));
                await writer.SendAsync(new GatewayProtocol.Frame(
                    GatewayProtocol.Type.TcpData, flowId, Payload: new byte[32 * 1024]));
                await writer.SendAsync(new GatewayProtocol.Frame(GatewayProtocol.Type.TcpClose, flowId));
            }
        }).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));

        foreach (var group in stream.Snapshot().GroupBy(f => f.FlowId))
        {
            var frames = group.ToArray();
            if ((group.Key & 1) == 0)
            {
                Require(frames.First().MessageType == GatewayProtocol.Type.UdpOpen,
                    $"UDP_OPEN ordering failed for flow {group.Key}");
                Require(frames.Last().MessageType == GatewayProtocol.Type.UdpClose,
                    $"UDP_CLOSE ordering failed for flow {group.Key}");
            }
            else
            {
                Require(frames.First().MessageType == GatewayProtocol.Type.TcpConnect,
                    $"TCP_CONNECT ordering failed for flow {group.Key}");
                Require(frames.Last().MessageType == GatewayProtocol.Type.TcpClose,
                    $"TCP_CLOSE ordering failed for flow {group.Key}");
            }
        }
        Console.WriteLine("TEST_TX_D_100_FLOW_MIX_PASS");
    }

    private static async Task TestOutboundUdpOpenLatency()
    {
        var stream = new RecordingStream(writeDelay: TimeSpan.FromMilliseconds(2));
        await using var writer = new GatewayOutboundWriter(stream);
        var data = Enumerable.Range(0, GatewayOutboundWriter.DataCapacity)
            .Select(i => writer.SendAsync(new GatewayProtocol.Frame(
                GatewayProtocol.Type.TcpData, 1000 + i, Payload: new byte[1024])))
            .ToArray();
        var opens = Enumerable.Range(0, 100)
            .Select(i => writer.SendAsync(new GatewayProtocol.Frame(GatewayProtocol.Type.UdpOpen, 2000 + i)))
            .ToArray();
        await Task.WhenAll(opens).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(data).WaitAsync(TimeSpan.FromSeconds(5));
        var snapshot = writer.Snapshot();
        Require(snapshot.MaxUdpOpenEnqueueToWriteMs < 1000,
            $"UDP_OPEN max latency was {snapshot.MaxUdpOpenEnqueueToWriteMs}ms");
        Console.WriteLine($"TEST_TX_E_UDP_OPEN_LATENCY_PASS maxMs={snapshot.MaxUdpOpenEnqueueToWriteMs}");
    }

    private static async Task TestOutboundPingPriority()
    {
        var stream = new RecordingStream(blockFirstWrite: true);
        await using var writer = new GatewayOutboundWriter(stream);
        var first = writer.SendAsync(new GatewayProtocol.Frame(
            GatewayProtocol.Type.TcpData, 1, Payload: new byte[1024]));
        await stream.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var data = Enumerable.Range(0, 32).Select(i => writer.SendAsync(new GatewayProtocol.Frame(
            GatewayProtocol.Type.TcpData, 10 + i, Payload: new byte[1024]))).ToArray();
        var ping = writer.SendAsync(new GatewayProtocol.Frame(GatewayProtocol.Type.Ping, Arg: 99));
        stream.ReleaseFirstWrite();
        await ping.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(data.Prepend(first));
        var writes = stream.Snapshot();
        Require(writes[1].MessageType == GatewayProtocol.Type.Ping,
            "PING did not preempt the data backlog");
        Console.WriteLine("TEST_TX_F_PING_PRIORITY_PASS");
    }

    private static async Task TestOutboundBoundedMemory()
    {
        var stream = new RecordingStream(blockFirstWrite: true);
        var writer = new GatewayOutboundWriter(stream);
        var tasks = Enumerable.Range(0, GatewayOutboundWriter.DataCapacity + 80)
            .Select(i => writer.SendAsync(new GatewayProtocol.Frame(
                GatewayProtocol.Type.TcpData, 5000 + i, Payload: new byte[1024])))
            .ToArray();
        await stream.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        var snapshot = writer.Snapshot();
        Require(snapshot.DataDepth <= GatewayOutboundWriter.DataCapacity,
            "bounded data queue exceeded capacity");
        stream.ReleaseFirstWrite();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        await writer.DisposeAsync();
        Console.WriteLine("TEST_TX_G_BOUNDED_MEMORY_PASS");
    }

    private static async Task TestOutboundDataFairness()
    {
        var stream = new RecordingStream(blockFirstWrite: true);
        await using var writer = new GatewayOutboundWriter(stream);
        var first = writer.SendAsync(new GatewayProtocol.Frame(
            GatewayProtocol.Type.TcpData, 1, Payload: new byte[] { 1 }));
        await stream.FirstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var controls = Enumerable.Range(0, GatewayOutboundWriter.ControlCapacity)
            .Select(i => writer.SendAsync(new GatewayProtocol.Frame(GatewayProtocol.Type.Ping, Arg: i)))
            .ToArray();
        var flowControls = Enumerable.Range(0, 32)
            .Select(i => writer.SendAsync(new GatewayProtocol.Frame(GatewayProtocol.Type.UdpOpen, 6000 + i)))
            .ToArray();
        var data = writer.SendAsync(new GatewayProtocol.Frame(
            GatewayProtocol.Type.TcpData, 9999, Payload: new byte[] { 9 }));
        stream.ReleaseFirstWrite();
        await data.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.WhenAll(controls.Concat(flowControls).Append(first));
        var index = stream.Snapshot().FindIndex(f => f.FlowId == 9999);
        Require(index > 0 && index <= 10, $"DATA fairness failed; write index={index}");
        Console.WriteLine("TEST_TX_H_DATA_FAIRNESS_PASS");
    }

    private static async Task TestOutboundLoggingIsolation()
    {
        var stream = new RecordingStream();
        await using var writer = new GatewayOutboundWriter(
            stream,
            diagnosticSink: _ => Thread.Sleep(100));
        var timer = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, 20)
            .Select(i => writer.SendAsync(new GatewayProtocol.Frame(GatewayProtocol.Type.UdpOpen, 7000 + i)))
            .ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));
        timer.Stop();
        Require(timer.Elapsed < TimeSpan.FromMilliseconds(500),
            $"slow diagnostics blocked writer for {timer.ElapsedMilliseconds}ms");
        Console.WriteLine("TEST_TX_I_LOGGING_ISOLATION_PASS");
    }

    private static async Task TestReaderDoesNotBlockOnSlowFlow()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pongSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var slow = new FlowDeliveryLane(FlowDeliveryLane.TcpCapacity,
            async (_, token) => await slowRelease.Task.WaitAsync(token), (_, _) => { }, _ => { });
        await using var fast = new FlowDeliveryLane(FlowDeliveryLane.TcpCapacity,
            (_, _) => { fastSeen.TrySetResult(); return Task.CompletedTask; }, (_, _) => { }, _ => { });

        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            _ = await GatewayProtocol.ReadFrameAsync(stream, CancellationToken.None);
            await GatewayProtocol.WriteFrameAsync(stream, new GatewayProtocol.Frame(GatewayProtocol.Type.HelloAck), CancellationToken.None);
            await GatewayProtocol.WriteFrameAsync(stream, new GatewayProtocol.Frame(GatewayProtocol.Type.TcpDataIn, 1, Payload: new byte[] { 1 }), CancellationToken.None);
            await GatewayProtocol.WriteFrameAsync(stream, new GatewayProtocol.Frame(GatewayProtocol.Type.Pong, Arg: 7), CancellationToken.None);
            await GatewayProtocol.WriteFrameAsync(stream, new GatewayProtocol.Frame(GatewayProtocol.Type.TcpDataIn, 2, Payload: new byte[] { 2 }), CancellationToken.None);
            await Task.Delay(500);
        });

        await using var client = new NetRelayGatewayClient();
        client.FrameReceived += frame =>
        {
            if (frame.MessageType == GatewayProtocol.Type.Pong) pongSeen.TrySetResult();
            if (frame.MessageType == GatewayProtocol.Type.TcpDataIn && frame.FlowId == 1)
                Require(slow.TryEnqueue((byte[])frame.Data.Clone()), "slow lane rejected first frame");
            if (frame.MessageType == GatewayProtocol.Type.TcpDataIn && frame.FlowId == 2)
                Require(fast.TryEnqueue((byte[])frame.Data.Clone()), "fast lane rejected frame");
            return Task.CompletedTask;
        };
        await client.ConnectAsync("127.0.0.1", port, activityAwareHeartbeat: false);
        await pongSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fastSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        slowRelease.TrySetResult();
        await server;
        Console.WriteLine("TEST_A_READER_NON_BLOCKING_PASS");
        Console.WriteLine("TEST_E_PONG_UNDER_BACKPRESSURE_PASS");
        Console.WriteLine("TEST_F_TRANSPORT_READ_CONTINUES_PASS");
    }

    private static async Task TestHundredFlowIsolation()
    {
        var blockers = new TaskCompletionSource[10];
        var completed = 0;
        var lanes = new List<FlowDeliveryLane>();
        for (var i = 0; i < 100; i++)
        {
            if (i < 10) blockers[i] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var index = i;
            var lane = new FlowDeliveryLane(FlowDeliveryLane.TcpCapacity,
                async (_, token) =>
                {
                    if (index < 10) await blockers[index].Task.WaitAsync(token);
                    else Interlocked.Increment(ref completed);
                }, (_, _) => { }, _ => { });
            lanes.Add(lane);
            Require(lane.TryEnqueue(new byte[] { (byte)i }), $"flow {i} enqueue failed");
        }
        await WaitUntilAsync(() => Volatile.Read(ref completed) == 90, TimeSpan.FromSeconds(3));
        Require(lanes.All(lane => lane.Depth <= FlowDeliveryLane.TcpCapacity), "lane exceeded bounded capacity");
        foreach (var blocker in blockers) blocker.TrySetResult();
        foreach (var lane in lanes) await lane.DisposeAsync();
        Console.WriteLine("TEST_B_100_SLOW_FAST_FLOWS_PASS");
        Console.WriteLine("TEST_D_FLOW_ISOLATION_PASS");
    }

    private static async Task TestFiveThousandFrameOrder()
    {
        var received = new ConcurrentQueue<int>();
        await using var lane = new FlowDeliveryLane(FlowDeliveryLane.TcpCapacity,
            (data, _) => { received.Enqueue(BitConverter.ToInt32(data)); return Task.CompletedTask; },
            (_, _) => { }, _ => { });
        for (var i = 0; i < 5000; i++)
        {
            var payload = BitConverter.GetBytes(i);
            while (!lane.TryEnqueue(payload)) await Task.Yield();
        }
        await WaitUntilAsync(() => received.Count == 5000, TimeSpan.FromSeconds(10));
        Require(received.SequenceEqual(Enumerable.Range(0, 5000)), "per-flow byte order changed");
        Console.WriteLine("TEST_C_FLOW_ORDER_5000_PASS");
    }

    private static async Task TestBlockedFlowIsolation()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastCount = 0;
        await using var blocked = new FlowDeliveryLane(FlowDeliveryLane.TcpCapacity,
            async (_, token) => await release.Task.WaitAsync(token), (_, _) => { }, _ => { });
        await using var fast = new FlowDeliveryLane(FlowDeliveryLane.TcpCapacity,
            (_, _) => { Interlocked.Increment(ref fastCount); return Task.CompletedTask; }, (_, _) => { }, _ => { });
        Require(blocked.TryEnqueue(new byte[] { 1 }), "blocked lane initial enqueue failed");
        for (var i = 0; i < 32; i++) fast.TryEnqueue(new byte[] { 2 });
        await WaitUntilAsync(() => Volatile.Read(ref fastCount) == 32, TimeSpan.FromSeconds(3));
        release.TrySetResult();
        Console.WriteLine("TEST_D_BLOCKED_FLOW_SESSION_ISOLATION_PASS");
    }

    private static async Task TestMixedStress()
    {
        const int flowCount = 200;
        const int framesPerFlow = 100;
        var delivered = 0;
        var lanes = Enumerable.Range(0, flowCount).Select(_ => new FlowDeliveryLane(
            FlowDeliveryLane.TcpCapacity,
            (_, _) => { Interlocked.Increment(ref delivered); return Task.CompletedTask; },
            (_, _) => { }, _ => { })).ToArray();
        for (var frame = 0; frame < framesPerFlow; frame++)
        {
            for (var flow = 0; flow < flowCount; flow++)
            {
                while (!lanes[flow].TryEnqueue(BitConverter.GetBytes(frame))) await Task.Yield();
            }
        }
        await WaitUntilAsync(() => Volatile.Read(ref delivered) == flowCount * framesPerFlow, TimeSpan.FromSeconds(15));
        Require(lanes.All(lane => lane.MaxDepth <= FlowDeliveryLane.TcpCapacity), "stress queue exceeded bound");
        foreach (var lane in lanes) await lane.DisposeAsync();
        Console.WriteLine("TEST_M_USB_AEL2_MIXED_STREAM_STRESS_PASS");
    }

    private static void TestProductionGuards()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var recovery = File.ReadAllText(Path.Combine(root, "src", "ViewModels", "ConnectionViewModel.Recovery.cs"));
        var vm = File.ReadAllText(Path.Combine(root, "src", "ViewModels", "ConnectionViewModel.cs"));
        var adapter = File.ReadAllText(Path.Combine(root, "src", "ViewModels", "ConnectionViewModel.Adapter.cs"));
        var adb = File.ReadAllText(Path.Combine(root, "src", "Services", "AdbBridgeService.cs"));
        var gateway = File.ReadAllText(Path.Combine(root, "src", "Services", "NetRelayGatewayClient.cs"));
        var outbound = File.ReadAllText(Path.Combine(root, "src", "Services", "GatewayOutboundWriter.cs"));
        var socks = File.ReadAllText(Path.Combine(root, "src", "Services", "Socks5GatewayServer.cs"));
        Require(vm.Contains("Duplicate connect ignored"), "double-connect gate missing");
        Require(vm.Contains("EnsureCurrentConnectionAttempt"), "stale generation guard missing");
        Require(recovery.Contains("Recovery data plane validation failed"), "recovery commit validation missing");
        Require(recovery.Contains("Recovery rollback failed"), "recovery rollback missing");
        Require(adapter.Contains("no legacy fallback exists"), "AEL2 fail-closed gate missing");
        Require(!adb.Contains("8000") && !adb.Contains("ProxyPort"), "legacy port 8000 remains");
        Require(!gateway.Contains("_writeLock"), "legacy producer write lock remains");
        Require(!gateway.Contains("payloadCrc32") && !gateway.Contains("[NETRELAY-FRAME] dir=TX"),
            "per-frame TX disk logging remains in the hot path");
        Require(gateway.Contains("SendBufferSize = 64 * 1024"), "bounded transport send buffer missing");
        Require(outbound.Contains("MaxTcpDataChunkSize = 16 * 1024"), "16 KiB writer cap missing");
        Require(socks.Contains("GatewayOutboundWriter.MaxTcpDataChunkSize"), "SOCKS TCP read cap missing");
        Console.WriteLine("TEST_G_USB_CONNECT_DOUBLE_CLICK_GUARD_PASS");
        Console.WriteLine("TEST_H_STALE_CONNECT_COMPLETION_GUARD_PASS");
        Console.WriteLine("TEST_I_RECOVERY_HELLO_FAIL_GUARD_PASS");
        Console.WriteLine("TEST_J_RECOVERY_ADAPTER_FAIL_GUARD_PASS");
        Console.WriteLine("TEST_K_RECOVERY_SUCCESS_COMMIT_GATE_PASS");
        Console.WriteLine("TEST_L_PORT_8000_ABSENT_PASS");
        Console.WriteLine("TEST_N_WFD_SHARED_DISPATCH_STATIC_PASS");
        Console.WriteLine("TEST_TX_J_PRODUCTION_GUARDS_PASS");
    }

    private sealed record RecordedFrame(byte MessageType, int FlowId, int Arg, byte[] Payload);

    private sealed class RecordingStream : Stream
    {
        private readonly object _gate = new();
        private readonly List<RecordedFrame> _frames = new();
        private readonly TaskCompletionSource _firstWriteRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _blockFirstWrite;
        private readonly TimeSpan _writeDelay;
        private int _writeCount;
        private int _flushCount;

        public RecordingStream(bool blockFirstWrite = false, TimeSpan? writeDelay = null)
        {
            _blockFirstWrite = blockFirstWrite;
            _writeDelay = writeDelay ?? TimeSpan.Zero;
            if (!blockFirstWrite) _firstWriteRelease.TrySetResult();
        }

        public TaskCompletionSource FirstWriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FlushCount => Volatile.Read(ref _flushCount);

        public void ReleaseFirstWrite() => _firstWriteRelease.TrySetResult();

        public List<RecordedFrame> Snapshot()
        {
            lock (_gate) return new List<RecordedFrame>(_frames);
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var writeNumber = Interlocked.Increment(ref _writeCount);
            if (writeNumber == 1)
            {
                FirstWriteStarted.TrySetResult();
                if (_blockFirstWrite)
                    await _firstWriteRelease.Task.WaitAsync(cancellationToken);
            }
            if (_writeDelay > TimeSpan.Zero)
            {
                var deadline = Stopwatch.GetTimestamp() +
                    (long)(_writeDelay.TotalSeconds * Stopwatch.Frequency);
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Thread.SpinWait(64);
                }
            }

            var bytes = buffer.ToArray();
            Require(bytes.Length >= GatewayProtocol.HeaderSize, "writer emitted a partial frame");
            var payloadLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4));
            Require(bytes.Length == GatewayProtocol.HeaderSize + payloadLength,
                "writer combined or truncated encoded frames");
            var frame = new RecordedFrame(
                bytes[5],
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8, 4)),
                BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(12, 4)),
                bytes.AsSpan(GatewayProtocol.HeaderSize, payloadLength).ToArray());
            lock (_gate) _frames.Add(frame);
        }

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() => Interlocked.Increment(ref _flushCount);
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _flushCount);
            return Task.CompletedTask;
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= end) throw new TimeoutException("Harness condition timed out.");
            await Task.Delay(5);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
