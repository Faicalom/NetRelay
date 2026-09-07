using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NetRelay.Services
{
    /// <summary>
    /// A bounded, single-consumer delivery lane. One slow local SOCKS consumer can
    /// back up only its own lane; it never blocks the shared AEL2 transport reader.
    /// </summary>
    internal sealed class FlowDeliveryLane : IAsyncDisposable
    {
        private sealed record DeliveryItem(byte[] Data, bool CloseAfter);

        private readonly Channel<DeliveryItem> _queue;
        private readonly Func<byte[], CancellationToken, Task> _deliver;
        private readonly Action<long, int> _onSlowWrite;
        private readonly Action<Exception?> _onTerminated;
        private readonly int _capacity;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _worker;
        private int _depth;
        private int _maxDepth;
        private int _terminated;

        public FlowDeliveryLane(
            int capacity,
            Func<byte[], CancellationToken, Task> deliver,
            Action<long, int> onSlowWrite,
            Action<Exception?> onTerminated)
        {
            _capacity = capacity;
            _deliver = deliver;
            _onSlowWrite = onSlowWrite;
            _onTerminated = onTerminated;
            _queue = Channel.CreateBounded<DeliveryItem>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
            _worker = Task.Run(WorkerAsync);
        }

        public int Depth => Volatile.Read(ref _depth);
        public int MaxDepth => Volatile.Read(ref _maxDepth);

        public bool TryEnqueue(byte[] data)
        {
            if (Volatile.Read(ref _terminated) != 0)
                return false;
            if (!TryReserveDepth(out var depth))
                return false;
            if (!_queue.Writer.TryWrite(new DeliveryItem(data, false)))
            {
                Interlocked.Decrement(ref _depth);
                return false;
            }
            UpdateMaxDepth(depth);
            return true;
        }

        public bool TryCompleteInOrder()
        {
            if (Volatile.Read(ref _terminated) != 0)
                return false;
            if (!TryReserveDepth(out var depth))
                return false;
            if (!_queue.Writer.TryWrite(new DeliveryItem(Array.Empty<byte>(), true)))
            {
                Interlocked.Decrement(ref _depth);
                return false;
            }
            UpdateMaxDepth(depth);
            return true;
        }

        private async Task WorkerAsync()
        {
            Exception? failure = null;
            try
            {
                await foreach (var item in _queue.Reader.ReadAllAsync(_cancellation.Token))
                {
                    Interlocked.Decrement(ref _depth);
                    if (item.CloseAfter)
                        break;

                    var started = Environment.TickCount64;
                    using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
                    writeTimeout.CancelAfter(LocalWriteTimeout);
                    await _deliver(item.Data, writeTimeout.Token).ConfigureAwait(false);
                    var durationMs = Math.Max(0, Environment.TickCount64 - started);
                    if (durationMs > SlowWriteWarningMs)
                        _onSlowWrite(durationMs, item.Data.Length);
                }
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (Interlocked.Exchange(ref _terminated, 1) == 0)
                {
                    _queue.Writer.TryComplete(failure);
                    while (_queue.Reader.TryRead(out _))
                        Interlocked.Decrement(ref _depth);
                    _onTerminated(failure);
                }
            }
        }

        private bool TryReserveDepth(out int depth)
        {
            while (true)
            {
                var current = Volatile.Read(ref _depth);
                if (current >= _capacity)
                {
                    depth = current;
                    return false;
                }

                depth = current + 1;
                if (Interlocked.CompareExchange(ref _depth, depth, current) == current)
                    return true;
            }
        }

        private void UpdateMaxDepth(int depth)
        {
            var current = Volatile.Read(ref _maxDepth);
            while (depth > current)
            {
                var observed = Interlocked.CompareExchange(ref _maxDepth, depth, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _queue.Writer.TryComplete();
            _cancellation.Cancel();
            try { await _worker.ConfigureAwait(false); } catch { }
            _cancellation.Dispose();
        }

        public const int TcpCapacity = 32;
        public const int UdpCapacity = 64;
        public const long SlowWriteWarningMs = 500;
        public static readonly TimeSpan LocalWriteTimeout = TimeSpan.FromSeconds(10);
    }
}
