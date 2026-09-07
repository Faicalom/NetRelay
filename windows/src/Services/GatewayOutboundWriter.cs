using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NetRelay.Services
{
    internal enum GatewayOutboundPriority
    {
        Control,
        FlowControl,
        Data
    }

    internal readonly record struct GatewayControlWriteEvent(
        byte MessageType,
        int FlowId,
        long EnqueueToWriteMs);

    internal readonly record struct GatewayOutboundWriterSnapshot(
        int ControlDepth,
        int FlowControlDepth,
        int DataDepth,
        long ControlOldestAgeMs,
        long FlowControlOldestAgeMs,
        long DataOldestAgeMs,
        long FramesWritten,
        long BytesWritten,
        long TcpChunksWritten,
        bool WriterRunning,
        long LastWriterProgressAgeMs,
        long MaxControlQueueAgeMs,
        long MaxFlowControlQueueAgeMs,
        long MaxDataQueueAgeMs,
        long MaxUdpOpenEnqueueToWriteMs,
        long MaxTcpConnectEnqueueToWriteMs);

    /// <summary>
    /// Session-scoped, bounded and priority-aware serializer for the one AEL2 byte stream.
    /// Once accepted, a frame is always completed by this writer (or failed by session shutdown),
    /// so producers can preserve per-flow ordering by awaiting SendAsync.
    /// </summary>
    internal sealed class GatewayOutboundWriter : IAsyncDisposable
    {
        internal const int ControlCapacity = 32;
        internal const int FlowControlCapacity = 256;
        internal const int DataCapacity = 128;
        internal const int MaxTcpDataChunkSize = 16 * 1024;
        private const int MaxControlBurst = 4;
        private const int MaxPriorityBurst = 8;
        private const int DiagnosticCapacity = 64;

        private sealed class QueuedFrame
        {
            public required long Sequence { get; init; }
            public required GatewayProtocol.Frame Frame { get; init; }
            public required GatewayOutboundPriority Priority { get; init; }
            public required byte[] Encoded { get; init; }
            public required long EnqueuedTimestamp { get; init; }
            public TaskCompletionSource Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class QueueState
        {
            public QueueState(int capacity)
            {
                Capacity = capacity;
                Spaces = new SemaphoreSlim(capacity, capacity);
            }

            public int Capacity { get; }
            public SemaphoreSlim Spaces { get; }
            public Queue<QueuedFrame> Items { get; } = new();
            public long MaxAgeMs;
        }

        private readonly Stream _stream;
        private readonly Action<GatewayControlWriteEvent>? _diagnosticSink;
        private readonly Action<Exception>? _failureSink;
        private readonly object _gate = new();
        private readonly QueueState _control = new(ControlCapacity);
        private readonly QueueState _flowControl = new(FlowControlCapacity);
        private readonly QueueState _data = new(DataCapacity);
        private readonly SemaphoreSlim _available = new(0);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly System.Threading.Channels.Channel<GatewayControlWriteEvent> _diagnostics =
            System.Threading.Channels.Channel.CreateBounded<GatewayControlWriteEvent>(
                new System.Threading.Channels.BoundedChannelOptions(DiagnosticCapacity)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite
                });
        private readonly Task _writerTask;
        private readonly Task _diagnosticTask;
        private long _sequence;
        private long _framesWritten;
        private long _bytesWritten;
        private long _tcpChunksWritten;
        private long _lastWriterProgressTimestamp;
        private long _maxUdpOpenLatencyMs;
        private long _maxTcpConnectLatencyMs;
        private int _writerRunning;
        private int _stopped;

        public GatewayOutboundWriter(
            Stream stream,
            Action<GatewayControlWriteEvent>? diagnosticSink = null,
            Action<Exception>? failureSink = null)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _diagnosticSink = diagnosticSink;
            _failureSink = failureSink;
            _lastWriterProgressTimestamp = Stopwatch.GetTimestamp();
            _writerTask = Task.Run(WriterLoopAsync);
            _diagnosticTask = Task.Run(DiagnosticLoopAsync);
        }

        public async Task SendAsync(
            GatewayProtocol.Frame frame,
            CancellationToken cancellationToken = default)
        {
            var owned = frame.OwnedCopy();
            if (owned.MessageType == GatewayProtocol.Type.TcpData &&
                owned.Data.Length > MaxTcpDataChunkSize)
            {
                for (var offset = 0; offset < owned.Data.Length; offset += MaxTcpDataChunkSize)
                {
                    var length = Math.Min(MaxTcpDataChunkSize, owned.Data.Length - offset);
                    var chunk = owned.Data.AsSpan(offset, length).ToArray();
                    await EnqueueOneAsync(
                        owned with { Payload = chunk },
                        cancellationToken).ConfigureAwait(false);
                }
                return;
            }

            await EnqueueOneAsync(owned, cancellationToken).ConfigureAwait(false);
        }

        public GatewayOutboundWriterSnapshot Snapshot()
        {
            var now = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                return new GatewayOutboundWriterSnapshot(
                    _control.Items.Count,
                    _flowControl.Items.Count,
                    _data.Items.Count,
                    OldestAgeMs(_control, now),
                    OldestAgeMs(_flowControl, now),
                    OldestAgeMs(_data, now),
                    Interlocked.Read(ref _framesWritten),
                    Interlocked.Read(ref _bytesWritten),
                    Interlocked.Read(ref _tcpChunksWritten),
                    Volatile.Read(ref _writerRunning) == 1,
                    ElapsedMs(Interlocked.Read(ref _lastWriterProgressTimestamp), now),
                    _control.MaxAgeMs,
                    _flowControl.MaxAgeMs,
                    _data.MaxAgeMs,
                    Interlocked.Read(ref _maxUdpOpenLatencyMs),
                    Interlocked.Read(ref _maxTcpConnectLatencyMs));
            }
        }

        private async Task EnqueueOneAsync(
            GatewayProtocol.Frame frame,
            CancellationToken cancellationToken)
        {
            ThrowIfStopped();
            var priority = PriorityFor(frame.MessageType);
            var queue = QueueFor(priority);
            using var enqueueCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _cancellation.Token);
            await queue.Spaces.WaitAsync(enqueueCancellation.Token).ConfigureAwait(false);

            QueuedFrame item;
            try
            {
                ThrowIfStopped();
                item = new QueuedFrame
                {
                    Sequence = Interlocked.Increment(ref _sequence),
                    Frame = frame,
                    Priority = priority,
                    Encoded = GatewayProtocol.EncodeFrame(frame),
                    EnqueuedTimestamp = Stopwatch.GetTimestamp()
                };
                lock (_gate)
                {
                    ThrowIfStopped();
                    queue.Items.Enqueue(item);
                }
            }
            catch
            {
                queue.Spaces.Release();
                throw;
            }

            _available.Release();
            await item.Completion.Task.ConfigureAwait(false);
        }

        private async Task WriterLoopAsync()
        {
            Interlocked.Exchange(ref _writerRunning, 1);
            Exception? failure = null;
            var priorityFramesSinceData = 0;
            var controlFramesSinceFlowControl = 0;
            try
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    await _available.WaitAsync(_cancellation.Token).ConfigureAwait(false);
                    var item = DequeueNext(priorityFramesSinceData, controlFramesSinceFlowControl);
                    if (item == null) continue;

                    var queueAgeMs = ElapsedMs(item.EnqueuedTimestamp, Stopwatch.GetTimestamp());
                    UpdateMax(ref QueueFor(item.Priority).MaxAgeMs, queueAgeMs);
                    try
                    {
                        // NetworkStream writes are immediately visible to the TCP stack; FlushAsync is a no-op.
                        // One write per encoded frame preserves framing while avoiding a redundant flush syscall path.
                        await GatewayProtocol.WriteEncodedFrameAsync(
                            _stream,
                            item.Encoded,
                            flush: false,
                            _cancellation.Token).ConfigureAwait(false);
                        Interlocked.Increment(ref _framesWritten);
                        Interlocked.Add(ref _bytesWritten, item.Encoded.Length);
                        if (item.Frame.MessageType == GatewayProtocol.Type.TcpData)
                            Interlocked.Increment(ref _tcpChunksWritten);
                        Interlocked.Exchange(ref _lastWriterProgressTimestamp, Stopwatch.GetTimestamp());
                        RecordControlLatency(item, queueAgeMs);
                        item.Completion.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        item.Completion.TrySetException(ex);
                        throw;
                    }

                    switch (item.Priority)
                    {
                        case GatewayOutboundPriority.Control:
                            priorityFramesSinceData++;
                            controlFramesSinceFlowControl++;
                            break;
                        case GatewayOutboundPriority.FlowControl:
                            priorityFramesSinceData++;
                            controlFramesSinceFlowControl = 0;
                            break;
                        default:
                            priorityFramesSinceData = 0;
                            controlFramesSinceFlowControl = 0;
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure = ex;
                _failureSink?.Invoke(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _writerRunning, 0);
                Interlocked.Exchange(ref _stopped, 1);
                FailQueued(failure ?? new OperationCanceledException("AEL2 outbound writer stopped."));
                _diagnostics.Writer.TryComplete();
            }
        }

        private QueuedFrame? DequeueNext(int priorityBurst, int controlBurst)
        {
            lock (_gate)
            {
                if (priorityBurst >= MaxPriorityBurst && TryDequeue(_data, out var fairData))
                    return fairData;
                if (controlBurst >= MaxControlBurst && TryDequeue(_flowControl, out var fairFlow))
                    return fairFlow;
                if (TryDequeue(_control, out var control)) return control;
                if (TryDequeue(_flowControl, out var flowControl)) return flowControl;
                if (TryDequeue(_data, out var data)) return data;
                return null;
            }
        }

        private static bool TryDequeue(QueueState queue, out QueuedFrame? item)
        {
            if (queue.Items.Count == 0)
            {
                item = null;
                return false;
            }
            item = queue.Items.Dequeue();
            queue.Spaces.Release();
            return true;
        }

        private void RecordControlLatency(QueuedFrame item, long latencyMs)
        {
            if (item.Frame.MessageType == GatewayProtocol.Type.UdpOpen)
                UpdateMax(ref _maxUdpOpenLatencyMs, latencyMs);
            else if (item.Frame.MessageType == GatewayProtocol.Type.TcpConnect)
                UpdateMax(ref _maxTcpConnectLatencyMs, latencyMs);
            else
                return;

            _diagnostics.Writer.TryWrite(new GatewayControlWriteEvent(
                item.Frame.MessageType,
                item.Frame.FlowId,
                latencyMs));
        }

        private async Task DiagnosticLoopAsync()
        {
            try
            {
                await foreach (var entry in _diagnostics.Reader.ReadAllAsync(_cancellation.Token))
                    _diagnosticSink?.Invoke(entry);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
        }

        private void FailQueued(Exception failure)
        {
            lock (_gate)
            {
                FailQueue(_control, failure);
                FailQueue(_flowControl, failure);
                FailQueue(_data, failure);
            }
        }

        private static void FailQueue(QueueState queue, Exception failure)
        {
            while (queue.Items.Count > 0)
            {
                var item = queue.Items.Dequeue();
                queue.Spaces.Release();
                item.Completion.TrySetException(failure);
            }
        }

        private static GatewayOutboundPriority PriorityFor(byte type) => type switch
        {
            GatewayProtocol.Type.Hello or GatewayProtocol.Type.Ping => GatewayOutboundPriority.Control,
            GatewayProtocol.Type.TcpConnect or GatewayProtocol.Type.TcpClose or
            GatewayProtocol.Type.UdpOpen or GatewayProtocol.Type.UdpClose or
            GatewayProtocol.Type.Error => GatewayOutboundPriority.FlowControl,
            GatewayProtocol.Type.TcpData or GatewayProtocol.Type.UdpDatagram => GatewayOutboundPriority.Data,
            _ => GatewayOutboundPriority.FlowControl
        };

        private QueueState QueueFor(GatewayOutboundPriority priority) => priority switch
        {
            GatewayOutboundPriority.Control => _control,
            GatewayOutboundPriority.FlowControl => _flowControl,
            _ => _data
        };

        private static long OldestAgeMs(QueueState queue, long now) =>
            queue.Items.Count == 0 ? 0 : ElapsedMs(queue.Items.Peek().EnqueuedTimestamp, now);

        private static long ElapsedMs(long start, long end) =>
            Math.Max(0, (long)((end - start) * 1000d / Stopwatch.Frequency));

        private static void UpdateMax(ref long target, long value)
        {
            var current = Interlocked.Read(ref target);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current) return;
                current = observed;
            }
        }

        private void ThrowIfStopped()
        {
            if (Volatile.Read(ref _stopped) != 0)
                throw new IOException("AEL2 outbound writer is not running.");
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                _cancellation.Cancel();
                try { _available.Release(); } catch (SemaphoreFullException) { }
            }
            try { await _writerTask.ConfigureAwait(false); } catch { }
            _diagnostics.Writer.TryComplete();
            try { await _diagnosticTask.ConfigureAwait(false); } catch { }
            _cancellation.Dispose();
            _available.Dispose();
            _control.Spaces.Dispose();
            _flowControl.Spaces.Dispose();
            _data.Spaces.Dispose();
        }
    }
}
