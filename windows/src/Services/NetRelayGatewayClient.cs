using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NetRelay.Utils;

namespace NetRelay.Services
{
    public readonly record struct GatewayFlowDispatchSnapshot(
        int ActiveTcpDeliveryLanes,
        int TcpQueuedFrames,
        int MaxTcpFlowQueueDepth,
        int ActiveUdpFlows,
        long SlowLocalFlowCount);

    /// <summary>
    /// One reliable multiplexed stream to the Android socket engine.
    /// TCP and UDP flows are frames on this stream, matching the architecture
    /// used by mature tethering tools while keeping NetRelay's own protocol.
    /// </summary>
    public sealed class NetRelayGatewayClient : IAsyncDisposable
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "NetRelayGatewayClient";
        private TcpClient? _client;
        private Stream? _stream;
        private GatewayOutboundWriter? _outboundWriter;
        private CancellationTokenSource? _readerCancellation;
        private Task? _readerTask;
        private CancellationTokenSource? _heartbeatCancellation;
        private Task? _heartbeatTask;
        private sealed record QueuedFrame(long Generation, GatewayProtocol.Frame Frame, bool IsControl);
        private Channel<QueuedFrame>? _controlDispatchQueue;
        private Channel<QueuedFrame>? _dataDispatchQueue;
        private SemaphoreSlim? _dispatchSignal;
        private CancellationTokenSource? _dispatchCancellation;
        private Task? _dispatchTask;
        private CancellationTokenSource? _metricsCancellation;
        private Task? _metricsTask;
        private readonly GatewayLivenessState _liveness = new();
        private int _isConnected;
        private int _readerRunning;
        private int _dispatchQueueDepth;
        private bool _activityAwareHeartbeat;
        private long _sessionGeneration;
        private long _framesRead;
        private long _framesQueued;
        private long _lastTransportReadTicks;
        private long _lastDispatchProgressTicks;
        private int _controlDispatchDepth;
        private int _dataDispatchDepth;

        public Func<int>? ActiveFlowCountProvider { get; set; }
        public Func<GatewayFlowDispatchSnapshot>? FlowDispatchSnapshotProvider { get; set; }

        public event Func<GatewayProtocol.Frame, Task>? FrameReceived;
        public event Action<Exception?>? Disconnected;

        public bool IsConnected => Volatile.Read(ref _isConnected) == 1 && _stream != null;

        public async Task ConnectAsync(
            string host,
            int port,
            CancellationToken cancellationToken = default,
            bool activityAwareHeartbeat = false)
        {
            if (port == 8992) Logger.Info(Tag, "[NETRELAY-USB] Opening NetRelayGatewayClient");
            if (port == 8991) Logger.Info(Tag, "[NETRELAY-WFD] Opening AEL2 session");
            await DisconnectAsync();

            var client = new TcpClient(AddressFamily.InterNetwork)
            {
                NoDelay = true,
                ReceiveBufferSize = 512 * 1024,
                // Bound bytes already committed ahead of a newly queued control frame.
                // USB/ADB and Wi-Fi Direct have a small BDP; 64 KiB retains throughput
                // while avoiding multi-second control HOL behind a 512 KiB send buffer.
                SendBufferSize = 64 * 1024
            };
            await client.ConnectAsync(host, port, cancellationToken);
            var stream = client.GetStream();

            _client = client;
            _stream = stream;
            _activityAwareHeartbeat = activityAwareHeartbeat;
            var generation = Interlocked.Increment(ref _sessionGeneration);
            _outboundWriter = new GatewayOutboundWriter(
                stream,
                diagnosticSink: entry =>
                {
                    var opcode = entry.MessageType == GatewayProtocol.Type.UdpOpen
                        ? "UDP_OPEN"
                        : "TCP_CONNECT";
                    Logger.Info(Tag,
                        $"[NETRELAY-TXQ] {opcode} WRITTEN flow={entry.FlowId} " +
                        $"enqueueToWriteMs={entry.EnqueueToWriteMs}");
                },
                failureSink: OnOutboundWriterFailure);

            await SendFrameAsync(
                new GatewayProtocol.Frame(
                    GatewayProtocol.Type.Hello,
                    Payload: Encoding.UTF8.GetBytes("NetRelay-PC-v2")),
                cancellationToken);
            if (port == 8992) Logger.Info(Tag, "[NETRELAY-USB] HELLO sent; waiting HELLO_ACK");
            if (port == 8991) Logger.Info(Tag, "[NETRELAY-WFD] HELLO sent");

            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            helloTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var hello = await GatewayProtocol.ReadFrameAsync(stream, helloTimeout.Token);
            if (hello?.MessageType != GatewayProtocol.Type.HelloAck)
            {
                await DisconnectAsync();
                throw new InvalidOperationException("Android gateway did not complete HELLO handshake.");
            }
            if (port == 8992) Logger.Info(Tag, "[NETRELAY-USB] HELLO_ACK received");
            if (port == 8991) Logger.Info(Tag, "[NETRELAY-WFD] HELLO_ACK received");

            Interlocked.Exchange(ref _isConnected, 1);
            _liveness.Reset(DateTime.UtcNow.Ticks);
            _controlDispatchQueue = CreateDispatchQueue(ControlDispatchCapacity);
            _dataDispatchQueue = CreateDispatchQueue(DataDispatchCapacity);
            _dispatchSignal = new SemaphoreSlim(0);
            _dispatchCancellation = new CancellationTokenSource();
            Interlocked.Exchange(ref _framesRead, 0);
            Interlocked.Exchange(ref _framesQueued, 0);
            Interlocked.Exchange(ref _controlDispatchDepth, 0);
            Interlocked.Exchange(ref _dataDispatchDepth, 0);
            Interlocked.Exchange(ref _lastTransportReadTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref _lastDispatchProgressTicks, DateTime.UtcNow.Ticks);
            _dispatchTask = DispatchLoopAsync(
                generation,
                _controlDispatchQueue.Reader,
                _dataDispatchQueue.Reader,
                _dispatchSignal,
                _dispatchCancellation.Token);
            _readerCancellation = new CancellationTokenSource();
            _readerTask = ReaderLoopAsync(generation, _readerCancellation.Token);
            _heartbeatCancellation = new CancellationTokenSource();
            _heartbeatTask = HeartbeatLoopAsync(_heartbeatCancellation.Token);
            _metricsCancellation = new CancellationTokenSource();
            _metricsTask = DispatchMetricsLoopAsync(_metricsCancellation.Token);
            Logger.Info(Tag, $"Connected to Android gateway {host}:{port}");
        }

        public async Task SendFrameAsync(
            GatewayProtocol.Frame frame,
            CancellationToken cancellationToken = default)
        {
            var writer = _outboundWriter ??
                throw new InvalidOperationException("Gateway outbound writer is not connected.");
            await writer.SendAsync(frame, cancellationToken).ConfigureAwait(false);
            if (_activityAwareHeartbeat)
                _liveness.RecordOutbound(DateTime.UtcNow.Ticks);
        }

        public async Task DisconnectAsync()
        {
            Interlocked.Exchange(ref _isConnected, 0);

            _heartbeatCancellation?.Cancel();
            _heartbeatCancellation?.Dispose();
            _heartbeatCancellation = null;

            _readerCancellation?.Cancel();
            _readerCancellation?.Dispose();
            _readerCancellation = null;

            _controlDispatchQueue?.Writer.TryComplete();
            _dataDispatchQueue?.Writer.TryComplete();
            _dispatchCancellation?.Cancel();
            try { _dispatchSignal?.Release(); } catch (ObjectDisposedException) { }

            _metricsCancellation?.Cancel();
            _metricsCancellation?.Dispose();
            _metricsCancellation = null;

            var outboundWriter = _outboundWriter;
            _outboundWriter = null;
            if (outboundWriter != null)
            {
                try { await outboundWriter.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            _stream?.Dispose();
            _stream = null;
            _client?.Dispose();
            _client = null;

            var heartbeat = _heartbeatTask;
            _heartbeatTask = null;
            if (heartbeat != null && heartbeat.Id != Task.CurrentId)
            {
                try { await heartbeat.ConfigureAwait(false); } catch { }
            }

            var reader = _readerTask;
            _readerTask = null;
            if (reader != null && reader.Id != Task.CurrentId)
            {
                try { await reader.ConfigureAwait(false); } catch { }
            }

            var dispatcher = _dispatchTask;
            _dispatchTask = null;
            if (dispatcher != null && dispatcher.Id != Task.CurrentId)
            {
                try { await dispatcher.ConfigureAwait(false); } catch { }
            }
            var metrics = _metricsTask;
            _metricsTask = null;
            if (metrics != null && metrics.Id != Task.CurrentId)
            {
                try { await metrics.ConfigureAwait(false); } catch { }
            }
            _controlDispatchQueue = null;
            _dataDispatchQueue = null;
            _dispatchCancellation?.Dispose();
            _dispatchCancellation = null;
            _dispatchSignal?.Dispose();
            _dispatchSignal = null;
            Interlocked.Exchange(ref _dispatchQueueDepth, 0);
            Interlocked.Exchange(ref _controlDispatchDepth, 0);
            Interlocked.Exchange(ref _dataDispatchDepth, 0);
            _activityAwareHeartbeat = false;
        }

        private async Task ReaderLoopAsync(long generation, CancellationToken cancellationToken)
        {
            Exception? failure = null;
            Interlocked.Exchange(ref _readerRunning, 1);
            try
            {
                var stream = _stream;
                if (stream == null)
                {
                    return;
                }
                while (!cancellationToken.IsCancellationRequested)
                {
                    var frame = await GatewayProtocol.ReadFrameAsync(stream, cancellationToken);
                    if (frame == null) break;
                    Interlocked.Increment(ref _framesRead);
                    Interlocked.Exchange(ref _lastTransportReadTicks, DateTime.UtcNow.Ticks);
                    if (generation != Interlocked.Read(ref _sessionGeneration))
                    {
                        Logger.Debug(Tag, $"[NETRELAY-FRAME] stale generation dropped generation={generation}");
                        continue;
                    }
                    if (_activityAwareHeartbeat)
                        _liveness.RecordInbound(DateTime.UtcNow.Ticks);
                    if (frame.MessageType == GatewayProtocol.Type.Pong)
                    {
                        if (_activityAwareHeartbeat)
                        {
                            var latencyMs = _liveness.RecordHeartbeatAck(frame.Arg, DateTime.UtcNow.Ticks);
                            Logger.Info(Tag, $"[NETRELAY-HB] heartbeat seq={frame.Arg} ack latencyMs={latencyMs}");
                        }
                    }

                    EnqueueDecodedFrame(generation, frame);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure = ex;
                Logger.Warn(Tag, $"Gateway reader stopped: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _readerRunning, 0);
                _controlDispatchQueue?.Writer.TryComplete(failure);
                _dataDispatchQueue?.Writer.TryComplete(failure);
                try { _dispatchSignal?.Release(); } catch (ObjectDisposedException) { }
                if (generation == Interlocked.Read(ref _sessionGeneration))
                    Interlocked.Exchange(ref _isConnected, 0);
                if (!cancellationToken.IsCancellationRequested &&
                    generation == Interlocked.Read(ref _sessionGeneration))
                {
                    Disconnected?.Invoke(failure);
                }
            }
        }

        private void EnqueueDecodedFrame(long generation, GatewayProtocol.Frame frame)
        {
            var isControl = IsControlFrame(frame.MessageType);
            var queue = isControl ? _controlDispatchQueue : _dataDispatchQueue;
            if (queue == null)
                throw new IOException("AEL2 dispatcher is not available.");

            var queued = new QueuedFrame(generation, frame.OwnedCopy(), isControl);
            if (!queue.Writer.TryWrite(queued))
                throw new IOException(isControl
                    ? "AEL2 control dispatch queue is full."
                    : "AEL2 data dispatch queue is full.");

            Interlocked.Increment(ref _framesQueued);
            Interlocked.Increment(ref _dispatchQueueDepth);
            if (isControl)
                Interlocked.Increment(ref _controlDispatchDepth);
            else
                Interlocked.Increment(ref _dataDispatchDepth);
            _dispatchSignal?.Release();
        }

        private async Task DispatchLoopAsync(
            long generation,
            ChannelReader<QueuedFrame> control,
            ChannelReader<QueuedFrame> data,
            SemaphoreSlim signal,
            CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                    QueuedFrame? queued = null;
                    if (!control.TryRead(out queued) && !data.TryRead(out queued))
                    {
                        if (control.Completion.IsCompleted && data.Completion.IsCompleted)
                            break;
                        continue;
                    }

                    Interlocked.Decrement(ref _dispatchQueueDepth);
                    if (queued.IsControl)
                        Interlocked.Decrement(ref _controlDispatchDepth);
                    else
                        Interlocked.Decrement(ref _dataDispatchDepth);
                    if (queued.Generation != generation || generation != Interlocked.Read(ref _sessionGeneration))
                    {
                        Logger.Debug(Tag, $"[NETRELAY-FRAME] stale queued frame dropped generation={queued.Generation}");
                        continue;
                    }
                    var frame = queued.Frame;
                    try
                    {
                        await DispatchFrameAsync(frame).ConfigureAwait(false);
                        Interlocked.Exchange(ref _lastDispatchProgressTicks, DateTime.UtcNow.Ticks);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug(Tag, $"Gateway frame dispatch failed flow={frame.FlowId}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private async Task DispatchMetricsLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    var now = DateTime.UtcNow.Ticks;
                    var flow = FlowDispatchSnapshotProvider?.Invoke() ?? default;
                    var tx = _outboundWriter?.Snapshot() ?? default;
                    Logger.Info(Tag,
                        $"[NETRELAY-DISPATCH] readerRunning={Volatile.Read(ref _readerRunning) == 1} " +
                        $"framesRead={Interlocked.Read(ref _framesRead)} framesQueued={Interlocked.Read(ref _framesQueued)} " +
                        $"controlDepth={Volatile.Read(ref _controlDispatchDepth)} flowControlDepth={Volatile.Read(ref _dataDispatchDepth)} " +
                        $"activeTcpDeliveryLanes={flow.ActiveTcpDeliveryLanes} tcpQueuedFrames={flow.TcpQueuedFrames} " +
                        $"maxTcpFlowQueueDepth={flow.MaxTcpFlowQueueDepth} activeUdpFlows={flow.ActiveUdpFlows} " +
                        $"lastTransportReadAgeMs={AgeMs(now, Interlocked.Read(ref _lastTransportReadTicks))} " +
                        $"lastDispatchProgressAgeMs={AgeMs(now, Interlocked.Read(ref _lastDispatchProgressTicks))} " +
                        $"slowLocalFlowCount={flow.SlowLocalFlowCount}");
                    Logger.Info(Tag,
                        $"[NETRELAY-TXQ] controlDepth={tx.ControlDepth} flowControlDepth={tx.FlowControlDepth} " +
                        $"dataDepth={tx.DataDepth} controlOldestAgeMs={tx.ControlOldestAgeMs} " +
                        $"flowControlOldestAgeMs={tx.FlowControlOldestAgeMs} dataOldestAgeMs={tx.DataOldestAgeMs} " +
                        $"framesWritten={tx.FramesWritten} bytesWritten={tx.BytesWritten} " +
                        $"tcpChunksWritten={tx.TcpChunksWritten} writerRunning={tx.WriterRunning} " +
                        $"lastWriterProgressAgeMs={tx.LastWriterProgressAgeMs} " +
                        $"maxControlQueueAgeMs={tx.MaxControlQueueAgeMs} " +
                        $"maxFlowControlQueueAgeMs={tx.MaxFlowControlQueueAgeMs} " +
                        $"maxDataQueueAgeMs={tx.MaxDataQueueAgeMs} " +
                        $"maxUdpOpenEnqueueToWriteMs={tx.MaxUdpOpenEnqueueToWriteMs} " +
                        $"maxTcpConnectEnqueueToWriteMs={tx.MaxTcpConnectEnqueueToWriteMs}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private static Channel<QueuedFrame> CreateDispatchQueue(int capacity) =>
            Channel.CreateBounded<QueuedFrame>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

        private static bool IsControlFrame(byte type) => type != GatewayProtocol.Type.TcpDataIn &&
            type != GatewayProtocol.Type.UdpDatagramIn;

        private static long AgeMs(long nowTicks, long thenTicks) =>
            thenTicks <= 0 ? 0 : Math.Max(0, (long)TimeSpan.FromTicks(nowTicks - thenTicks).TotalMilliseconds);

        private async Task DispatchFrameAsync(GatewayProtocol.Frame frame)
        {
            var handler = FrameReceived;
            if (handler == null) return;
            foreach (var subscriber in handler.GetInvocationList())
                await ((Func<GatewayProtocol.Frame, Task>)subscriber)(frame);
        }

        private const int ControlDispatchCapacity = 512;
        private const int DataDispatchCapacity = 4096;

        private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
        {
            var sequence = 0;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(GatewayLivenessState.HeartbeatInterval, cancellationToken);
                    if (!IsConnected) return;

                    var nowTicks = DateTime.UtcNow.Ticks;
                    if (_activityAwareHeartbeat)
                    {
                        var snapshot = _liveness.Capture(nowTicks);
                        Logger.Info(Tag,
                            $"[NETRELAY-HB] lastInboundAgeMs={snapshot.LastInboundAgeMs} " +
                            $"lastOutboundAgeMs={snapshot.LastOutboundAgeMs} " +
                            $"lastHeartbeatSentAgeMs={snapshot.LastHeartbeatSentAgeMs} " +
                            $"lastHeartbeatAckAgeMs={snapshot.LastHeartbeatAckAgeMs} " +
                            $"activeFlows={ActiveFlowCountProvider?.Invoke() ?? 0} " +
                            $"readerRunning={Volatile.Read(ref _readerRunning) == 1} " +
                            $"writerQueueDepth={OutboundQueueDepth()} " +
                            $"dispatchQueueDepth={Volatile.Read(ref _dispatchQueueDepth)} " +
                            $"gatewayConnected={IsConnected}");

                        if (_liveness.IsTimeoutCandidate(nowTicks))
                        {
                            Logger.Warn(Tag,
                                $"[NETRELAY-HB] timeout candidate lastInboundAgeMs={snapshot.LastInboundAgeMs} " +
                                $"lastAckAgeMs={snapshot.LastHeartbeatAckAgeMs} " +
                                $"lastSuccessfulReadAgeMs={snapshot.LastInboundAgeMs} " +
                                $"readerRunning={Volatile.Read(ref _readerRunning) == 1} " +
                                $"activeFlows={ActiveFlowCountProvider?.Invoke() ?? 0}");
                            Logger.Warn(Tag, "Android gateway heartbeat timed out");
                            Interlocked.Exchange(ref _isConnected, 0);
                            try { _client?.Dispose(); } catch { }
                            return;
                        }

                        if (snapshot.LastInboundAgeMs >= 0 &&
                            snapshot.LastInboundAgeMs < GatewayLivenessState.HeartbeatInterval.TotalMilliseconds)
                        {
                            continue;
                        }
                    }

                    var heartbeatSequence = Interlocked.Increment(ref sequence);
                    await SendFrameAsync(
                        new GatewayProtocol.Frame(
                            GatewayProtocol.Type.Ping,
                            Arg: heartbeatSequence),
                        cancellationToken);
                    if (_activityAwareHeartbeat)
                    {
                        _liveness.RecordHeartbeatSent(heartbeatSequence, DateTime.UtcNow.Ticks);
                        Logger.Info(Tag, $"[NETRELAY-HB] heartbeat seq={heartbeatSequence} sent");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Logger.Warn(Tag, $"Gateway heartbeat stopped: {ex.Message}");
                Interlocked.Exchange(ref _isConnected, 0);
                try { _client?.Dispose(); } catch { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await DisconnectAsync();
        }

        private int OutboundQueueDepth()
        {
            var snapshot = _outboundWriter?.Snapshot() ?? default;
            return snapshot.ControlDepth + snapshot.FlowControlDepth + snapshot.DataDepth;
        }

        private void OnOutboundWriterFailure(Exception failure)
        {
            Logger.Warn(Tag, $"AEL2 outbound writer stopped: {failure.Message}");
            Interlocked.Exchange(ref _isConnected, 0);
            try { _client?.Dispose(); } catch { }
            Disconnected?.Invoke(failure);
        }
    }
}
