using System;
using System.Threading;

namespace NetRelay.Services
{
    /// <summary>
    /// Tracks peer liveness independently from flow delivery. A valid inbound AEL2
    /// frame proves that the gateway is alive even when a dedicated PONG is delayed.
    /// </summary>
    public sealed class GatewayLivenessState
    {
        public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(18);

        private long _lastInboundTicks;
        private long _lastOutboundTicks;
        private long _lastHeartbeatSentTicks;
        private long _lastHeartbeatAckTicks;
        private int _lastHeartbeatSequence;

        public void Reset(long nowTicks)
        {
            Interlocked.Exchange(ref _lastInboundTicks, nowTicks);
            Interlocked.Exchange(ref _lastOutboundTicks, nowTicks);
            Interlocked.Exchange(ref _lastHeartbeatSentTicks, 0);
            Interlocked.Exchange(ref _lastHeartbeatAckTicks, nowTicks);
            Interlocked.Exchange(ref _lastHeartbeatSequence, 0);
        }

        public void RecordInbound(long nowTicks) =>
            Interlocked.Exchange(ref _lastInboundTicks, nowTicks);

        public void RecordOutbound(long nowTicks) =>
            Interlocked.Exchange(ref _lastOutboundTicks, nowTicks);

        public void RecordHeartbeatSent(int sequence, long nowTicks)
        {
            Interlocked.Exchange(ref _lastHeartbeatSequence, sequence);
            Interlocked.Exchange(ref _lastHeartbeatSentTicks, nowTicks);
        }

        public long RecordHeartbeatAck(int sequence, long nowTicks)
        {
            RecordInbound(nowTicks);
            Interlocked.Exchange(ref _lastHeartbeatAckTicks, nowTicks);
            var sentTicks = Interlocked.Read(ref _lastHeartbeatSentTicks);
            var sentSequence = Volatile.Read(ref _lastHeartbeatSequence);
            return sentTicks > 0 && sentSequence == sequence
                ? AgeMilliseconds(nowTicks, sentTicks)
                : -1;
        }

        public Snapshot Capture(long nowTicks) => new(
            AgeMilliseconds(nowTicks, Interlocked.Read(ref _lastInboundTicks)),
            AgeMilliseconds(nowTicks, Interlocked.Read(ref _lastOutboundTicks)),
            AgeMilliseconds(nowTicks, Interlocked.Read(ref _lastHeartbeatSentTicks)),
            AgeMilliseconds(nowTicks, Interlocked.Read(ref _lastHeartbeatAckTicks)));

        public bool IsTimeoutCandidate(long nowTicks)
        {
            var snapshot = Capture(nowTicks);
            var timeoutMs = (long)HeartbeatTimeout.TotalMilliseconds;
            return snapshot.LastInboundAgeMs > timeoutMs &&
                   snapshot.LastHeartbeatAckAgeMs > timeoutMs;
        }

        private static long AgeMilliseconds(long nowTicks, long previousTicks)
        {
            if (previousTicks <= 0 || nowTicks <= previousTicks)
                return previousTicks <= 0 ? -1 : 0;
            return (long)TimeSpan.FromTicks(nowTicks - previousTicks).TotalMilliseconds;
        }

        public readonly record struct Snapshot(
            long LastInboundAgeMs,
            long LastOutboundAgeMs,
            long LastHeartbeatSentAgeMs,
            long LastHeartbeatAckAgeMs);
    }
}
