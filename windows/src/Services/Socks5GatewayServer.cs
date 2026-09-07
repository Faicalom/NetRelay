using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NetRelay.Utils;

namespace NetRelay.Services
{
    /// <summary>
    /// Local SOCKS5 endpoint consumed by tun2socks.
    /// It does not connect to the Internet itself: CONNECT/UDP requests are translated
    /// into NetRelay v2 frames and executed on the Android phone.
    /// </summary>
    public sealed class Socks5GatewayServer : IAsyncDisposable
    {
        private static readonly Logger Logger = Logger.Instance;
        private const string Tag = "Socks5GatewayServer";
        private readonly NetRelayGatewayClient _gateway;
        private readonly ConcurrentDictionary<int, FlowContext> _flows = new();
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private int _nextFlowId = 100;
        private long _slowLocalFlowCount;
        private long _totalTcpFlows;
        private long _totalUdpFlows;

        public Socks5GatewayServer(NetRelayGatewayClient gateway)
        {
            _gateway = gateway;
            _gateway.FrameReceived += HandleGatewayFrameAsync;
            _gateway.Disconnected += HandleGatewayDisconnected;
            _gateway.FlowDispatchSnapshotProvider = CaptureDispatchSnapshot;
        }

        public bool IsRunning => _listener != null;
        public int ActiveFlowCount => _flows.Count;
        public SocksFlowMetrics FlowMetrics
        {
            get
            {
                var activeTcp = 0;
                var activeUdp = 0;
                foreach (var flow in _flows.Values)
                {
                    if (flow.LocalStream != null) activeTcp++;
                    if (flow.UdpClient != null) activeUdp++;
                }
                return new SocksFlowMetrics(
                    activeTcp,
                    activeUdp,
                    Interlocked.Read(ref _totalTcpFlows),
                    Interlocked.Read(ref _totalUdpFlows));
            }
        }
        public string Endpoint => $"socks5://127.0.0.1:{ListenPort}";

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_listener != null)
                return Task.CompletedTask;
            if (!_gateway.IsConnected)
                throw new InvalidOperationException("Android gateway is not connected.");

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Interlocked.Exchange(ref _totalTcpFlows, 0);
            Interlocked.Exchange(ref _totalUdpFlows, 0);
            _listener = new TcpListener(IPAddress.Loopback, ListenPort);
            _listener.Start(128);
            _ = AcceptLoopAsync(_cts.Token);
            Logger.Info(Tag, $"Local SOCKS5 gateway listening on 127.0.0.1:{ListenPort}");
            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _listener?.Stop();
            _listener = null;

            foreach (var flow in _flows.Values)
            {
                await flow.CloseLocalAsync();
            }
            _flows.Clear();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _listener != null)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    client.NoDelay = true;
                    _ = Task.Run(() => HandleSocksClientAsync(client, cancellationToken), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!cancellationToken.IsCancellationRequested)
                        Logger.Error(Tag, "SOCKS accept loop error", ex);
                }
            }
        }

        private async Task HandleSocksClientAsync(TcpClient client, CancellationToken serverCancellation)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    if (!await NegotiateAsync(stream, serverCancellation))
                        return;

                    var request = await ReadRequestAsync(stream, serverCancellation);
                    if (request == null)
                        return;

                    switch (request.Command)
                    {
                        case 0x01:
                            await HandleTcpConnectAsync(client, stream, request, serverCancellation);
                            break;
                        case 0x03:
                            await HandleUdpAssociateAsync(client, stream, serverCancellation);
                            break;
                        default:
                            await WriteReplyAsync(stream, 0x07, IPAddress.Any, 0, serverCancellation);
                            break;
                    }
                }
                catch (OperationCanceledException) when (serverCancellation.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    Logger.Debug(Tag, $"SOCKS client ended: {ex.Message}");
                }
            }
        }

        private static async Task<bool> NegotiateAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var head = new byte[2];
            await ReadExactlyAsync(stream, head, cancellationToken);
            if (head[0] != 0x05)
                return false;
            var methodCount = head[1];
            if (methodCount == 0)
                return false;
            var methods = new byte[methodCount];
            await ReadExactlyAsync(stream, methods, cancellationToken);
            var supportsNoAuth = Array.IndexOf(methods, (byte)0x00) >= 0;
            await stream.WriteAsync(new byte[] { 0x05, supportsNoAuth ? (byte)0x00 : (byte)0xFF }, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return supportsNoAuth;
        }

        private async Task HandleTcpConnectAsync(
            TcpClient localClient,
            NetworkStream localStream,
            SocksRequest request,
            CancellationToken cancellationToken)
        {
            var flowId = NextFlowId();
            var flow = FlowContext.ForTcp(flowId, localClient, localStream);
            flow.DeliveryLane = CreateTcpDeliveryLane(flow);
            _flows[flowId] = flow;

            try
            {
                await _gateway.SendFrameAsync(
                    new GatewayProtocol.Frame(
                        GatewayProtocol.Type.TcpConnect,
                        flowId,
                        request.Port,
                        Payload: GatewayProtocol.EncodeHost(request.Host)),
                    cancellationToken);

                var status = await flow.WaitOpenAsync(OpenTimeout, cancellationToken);
                if (status != GatewayProtocol.Status.Ok)
                {
                    await WriteReplyAsync(localStream, MapStatusToSocks(status), IPAddress.Any, 0, cancellationToken);
                    return;
                }

                Interlocked.Increment(ref _totalTcpFlows);

                await WriteReplyAsync(localStream, 0x00, IPAddress.Loopback, 0, cancellationToken);
                // Keep preemption granularity bounded on the shared AEL2 stream.
                // The outbound writer also enforces this cap defensively.
                var buffer = new byte[GatewayOutboundWriter.MaxTcpDataChunkSize];
                while (!cancellationToken.IsCancellationRequested)
                {
                    var count = await localStream.ReadAsync(buffer, cancellationToken);
                    if (count <= 0) break;
                    await _gateway.SendFrameAsync(
                        new GatewayProtocol.Frame(
                            GatewayProtocol.Type.TcpData,
                            flowId,
                            Payload: buffer.AsSpan(0, count).ToArray()),
                        cancellationToken);
                }
            }
            catch (IOException)
            {
            }
            finally
            {
                _flows.TryRemove(flowId, out _);
                try
                {
                    await _gateway.SendFrameAsync(new GatewayProtocol.Frame(GatewayProtocol.Type.TcpClose, flowId), cancellationToken);
                }
                catch { }
                await flow.CloseLocalAsync();
            }
        }

        private async Task HandleUdpAssociateAsync(
            TcpClient controlClient,
            NetworkStream controlStream,
            CancellationToken cancellationToken)
        {
            var flowId = NextFlowId();
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var localEndPoint = (IPEndPoint)udp.Client.LocalEndPoint!;
            var flow = FlowContext.ForUdp(flowId, controlClient, controlStream, udp);
            flow.DeliveryLane = CreateUdpDeliveryLane(flow);
            _flows[flowId] = flow;

            try
            {
                await _gateway.SendFrameAsync(
                    new GatewayProtocol.Frame(GatewayProtocol.Type.UdpOpen, flowId),
                    cancellationToken);
                var status = await flow.WaitOpenAsync(OpenTimeout, cancellationToken);
                if (status != GatewayProtocol.Status.Ok)
                {
                    await WriteReplyAsync(controlStream, MapStatusToSocks(status), IPAddress.Any, 0, cancellationToken);
                    return;
                }

                Interlocked.Increment(ref _totalUdpFlows);

                await WriteReplyAsync(
                    controlStream,
                    0x00,
                    localEndPoint.Address,
                    localEndPoint.Port,
                    cancellationToken);

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var relayTask = RelayUdpFromTun2SocksAsync(flow, linked.Token);
                var monitorTask = MonitorUdpControlConnectionAsync(controlStream, linked.Token);
                await Task.WhenAny(relayTask, monitorTask);
                await linked.CancelAsync();
                await IgnoreAsync(relayTask);
                await IgnoreAsync(monitorTask);
            }
            finally
            {
                _flows.TryRemove(flowId, out _);
                try
                {
                    await _gateway.SendFrameAsync(new GatewayProtocol.Frame(GatewayProtocol.Type.UdpClose, flowId), cancellationToken);
                }
                catch { }
                await flow.CloseLocalAsync();
            }
        }

        private async Task RelayUdpFromTun2SocksAsync(FlowContext flow, CancellationToken cancellationToken)
        {
            var udp = flow.UdpClient ?? throw new InvalidOperationException();
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(cancellationToken);
                flow.UdpClientEndpoint = result.RemoteEndPoint;
                var request = ParseSocksUdpPacket(result.Buffer);
                if (request == null || request.Fragment != 0)
                    continue;

                await _gateway.SendFrameAsync(
                    new GatewayProtocol.Frame(
                        GatewayProtocol.Type.UdpDatagram,
                        flow.FlowId,
                        Payload: GatewayProtocol.EncodeDatagram(request.Host, request.Port, request.Data)),
                    cancellationToken);
            }
        }

        private static async Task MonitorUdpControlConnectionAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var one = new byte[1];
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(one, cancellationToken);
                if (read == 0)
                    return;
                // RFC1928 clients normally send no payload after UDP ASSOCIATE.
                // Ignore any stray byte and keep the association alive.
            }
        }

        private Task HandleGatewayFrameAsync(GatewayProtocol.Frame frame)
        {
            if (!_flows.TryGetValue(frame.FlowId, out var flow))
                return Task.CompletedTask;

            try
            {
                switch (frame.MessageType)
                {
                    case GatewayProtocol.Type.TcpConnectResult:
                    case GatewayProtocol.Type.UdpOpenResult:
                        flow.OpenResult.TrySetResult(frame.Arg);
                        break;

                    case GatewayProtocol.Type.TcpDataIn:
                        if (flow.DeliveryLane == null || !flow.DeliveryLane.TryEnqueue((byte[])frame.Data.Clone()))
                        {
                            Logger.Warn(Tag, $"[NETRELAY-FLOW] FLOW_QUEUE_FULL flow={frame.FlowId}; closing only this flow");
                            FailFlow(flow, new IOException("Bounded TCP delivery lane is full."));
                        }
                        break;

                    case GatewayProtocol.Type.UdpDatagramIn:
                        if (flow.UdpClient != null && flow.UdpClientEndpoint != null)
                        {
                            var datagram = GatewayProtocol.DecodeDatagram(frame.Data);
                            var response = BuildSocksUdpPacket(datagram.Host, datagram.Port, datagram.Data);
                            if (flow.DeliveryLane == null || !flow.DeliveryLane.TryEnqueue(response))
                            {
                                Logger.Warn(Tag, $"[NETRELAY-FLOW] UDP_QUEUE_FULL flow={frame.FlowId}; closing only this flow");
                                FailFlow(flow, new IOException("Bounded UDP delivery lane is full."));
                            }
                        }
                        break;

                    case GatewayProtocol.Type.FlowClosed:
                    case GatewayProtocol.Type.Error:
                        flow.OpenResult.TrySetResult(frame.Arg == 0 ? GatewayProtocol.Status.GeneralFailure : frame.Arg);
                        if (flow.DeliveryLane == null || !flow.DeliveryLane.TryCompleteInOrder())
                            FailFlow(flow, null);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(Tag, $"Failed to deliver gateway frame flow={frame.FlowId}: {ex.Message}");
                FailFlow(flow, ex);
            }
            return Task.CompletedTask;
        }

        private FlowDeliveryLane CreateTcpDeliveryLane(FlowContext flow) => new(
            FlowDeliveryLane.TcpCapacity,
            async (data, cancellationToken) =>
            {
                var stream = flow.LocalStream ?? throw new IOException("Local TCP stream is closed.");
                await stream.WriteAsync(data.AsMemory(), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            },
            (durationMs, bytes) =>
            {
                Interlocked.Increment(ref _slowLocalFlowCount);
                var severity = durationMs > 2_000 ? "CRITICAL" : "WARN";
                Logger.Warn(Tag,
                    $"[NETRELAY-FLOW] SLOW_LOCAL_WRITE severity={severity} flow={flow.FlowId} " +
                    $"durationMs={durationMs} bytes={bytes}");
            },
            failure => FailFlow(flow, failure));

        private FlowDeliveryLane CreateUdpDeliveryLane(FlowContext flow) => new(
            FlowDeliveryLane.UdpCapacity,
            async (data, cancellationToken) =>
            {
                var udp = flow.UdpClient ?? throw new IOException("Local UDP socket is closed.");
                var endpoint = flow.UdpClientEndpoint ?? throw new IOException("Local UDP endpoint is unavailable.");
                await udp.SendAsync(data, endpoint, cancellationToken).ConfigureAwait(false);
            },
            (durationMs, bytes) =>
            {
                Interlocked.Increment(ref _slowLocalFlowCount);
                Logger.Warn(Tag,
                    $"[NETRELAY-FLOW] SLOW_LOCAL_WRITE severity={(durationMs > 2_000 ? "CRITICAL" : "WARN")} " +
                    $"flow={flow.FlowId} durationMs={durationMs} bytes={bytes}");
            },
            failure => FailFlow(flow, failure));

        private void FailFlow(FlowContext flow, Exception? failure)
        {
            if (!_flows.TryRemove(flow.FlowId, out _))
                return;
            if (failure != null)
                Logger.Debug(Tag, $"Local flow {flow.FlowId} terminated: {failure.Message}");
            flow.OpenResult.TrySetResult(GatewayProtocol.Status.GeneralFailure);
            flow.CloseSockets();
            _ = flow.DisposeLaneAsync();
        }

        private GatewayFlowDispatchSnapshot CaptureDispatchSnapshot()
        {
            var tcpLanes = 0;
            var tcpQueued = 0;
            var maxTcpDepth = 0;
            var udpFlows = 0;
            foreach (var flow in _flows.Values)
            {
                if (flow.LocalStream != null)
                {
                    tcpLanes++;
                    var depth = flow.DeliveryLane?.Depth ?? 0;
                    tcpQueued += depth;
                    maxTcpDepth = Math.Max(maxTcpDepth, flow.DeliveryLane?.MaxDepth ?? 0);
                }
                if (flow.UdpClient != null)
                    udpFlows++;
            }
            return new GatewayFlowDispatchSnapshot(
                tcpLanes,
                tcpQueued,
                maxTcpDepth,
                udpFlows,
                Interlocked.Read(ref _slowLocalFlowCount));
        }

        private void HandleGatewayDisconnected(Exception? error)
        {
            Logger.Warn(Tag, error == null ? "Android gateway disconnected" : $"Android gateway disconnected: {error.Message}");
            foreach (var flow in _flows.Values)
            {
                flow.OpenResult.TrySetResult(GatewayProtocol.Status.GeneralFailure);
                _ = flow.CloseLocalAsync();
            }
            _flows.Clear();
        }

        private static async Task<SocksRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var header = new byte[4];
            await ReadExactlyAsync(stream, header, cancellationToken);
            if (header[0] != 0x05 || header[2] != 0x00)
                return null;

            var host = await ReadAddressAsync(stream, header[3], cancellationToken);
            if (host == null)
                return null;
            var portBytes = new byte[2];
            await ReadExactlyAsync(stream, portBytes, cancellationToken);
            var port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);
            return new SocksRequest(header[1], host, port);
        }

        private static async Task<string?> ReadAddressAsync(Stream stream, byte atyp, CancellationToken cancellationToken)
        {
            switch (atyp)
            {
                case 0x01:
                {
                    var bytes = new byte[4];
                    await ReadExactlyAsync(stream, bytes, cancellationToken);
                    return new IPAddress(bytes).ToString();
                }
                case 0x03:
                {
                    var lengthByte = new byte[1];
                    await ReadExactlyAsync(stream, lengthByte, cancellationToken);
                    var bytes = new byte[lengthByte[0]];
                    await ReadExactlyAsync(stream, bytes, cancellationToken);
                    return Encoding.UTF8.GetString(bytes);
                }
                case 0x04:
                {
                    var bytes = new byte[16];
                    await ReadExactlyAsync(stream, bytes, cancellationToken);
                    return new IPAddress(bytes).ToString();
                }
                default:
                    return null;
            }
        }

        private static async Task WriteReplyAsync(
            NetworkStream stream,
            byte result,
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            var addressBytes = address.AddressFamily == AddressFamily.InterNetworkV6
                ? address.GetAddressBytes()
                : (address.MapToIPv4()).GetAddressBytes();
            var atyp = addressBytes.Length == 16 ? (byte)0x04 : (byte)0x01;
            var response = new byte[4 + addressBytes.Length + 2];
            response[0] = 0x05;
            response[1] = result;
            response[2] = 0x00;
            response[3] = atyp;
            addressBytes.CopyTo(response, 4);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4 + addressBytes.Length, 2), (ushort)Math.Clamp(port, 0, 65535));
            await stream.WriteAsync(response, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        private static SocksUdpPacket? ParseSocksUdpPacket(byte[] packet)
        {
            if (packet.Length < 7 || packet[0] != 0 || packet[1] != 0)
                return null;
            var fragment = packet[2];
            var atyp = packet[3];
            var offset = 4;
            string host;

            switch (atyp)
            {
                case 0x01:
                    if (packet.Length < offset + 4 + 2) return null;
                    host = new IPAddress(packet.AsSpan(offset, 4)).ToString();
                    offset += 4;
                    break;
                case 0x03:
                    if (packet.Length < offset + 1) return null;
                    var length = packet[offset++];
                    if (packet.Length < offset + length + 2) return null;
                    host = Encoding.UTF8.GetString(packet, offset, length);
                    offset += length;
                    break;
                case 0x04:
                    if (packet.Length < offset + 16 + 2) return null;
                    host = new IPAddress(packet.AsSpan(offset, 16)).ToString();
                    offset += 16;
                    break;
                default:
                    return null;
            }

            var port = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
            offset += 2;
            return new SocksUdpPacket(fragment, host, port, packet.AsSpan(offset).ToArray());
        }

        private static byte[] BuildSocksUdpPacket(string host, int port, byte[] data)
        {
            byte atyp;
            byte[] addressPart;
            if (IPAddress.TryParse(host, out var ip))
            {
                var bytes = ip.GetAddressBytes();
                atyp = bytes.Length == 16 ? (byte)0x04 : (byte)0x01;
                addressPart = bytes;
            }
            else
            {
                var hostBytes = Encoding.UTF8.GetBytes(host);
                if (hostBytes.Length > 255) throw new InvalidDataException("SOCKS UDP hostname too long");
                atyp = 0x03;
                addressPart = new byte[1 + hostBytes.Length];
                addressPart[0] = (byte)hostBytes.Length;
                hostBytes.CopyTo(addressPart, 1);
            }

            var result = new byte[4 + addressPart.Length + 2 + data.Length];
            result[0] = 0;
            result[1] = 0;
            result[2] = 0;
            result[3] = atyp;
            addressPart.CopyTo(result, 4);
            var portOffset = 4 + addressPart.Length;
            BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(portOffset, 2), (ushort)port);
            data.CopyTo(result, portOffset + 2);
            return result;
        }

        private int NextFlowId()
        {
            var next = Interlocked.Increment(ref _nextFlowId);
            if (next == int.MaxValue)
                Interlocked.Exchange(ref _nextFlowId, 100);
            return next;
        }

        private static byte MapStatusToSocks(int status) => status switch
        {
            GatewayProtocol.Status.NetworkUnreachable => 0x03,
            GatewayProtocol.Status.HostUnreachable => 0x04,
            GatewayProtocol.Status.ConnectionRefused => 0x05,
            GatewayProtocol.Status.TtlExpired => 0x06,
            GatewayProtocol.Status.CommandNotSupported => 0x07,
            GatewayProtocol.Status.AddressNotSupported => 0x08,
            _ => 0x01
        };

        private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
        }

        private static async Task IgnoreAsync(Task task)
        {
            try { await task; } catch { }
        }

        private sealed class FlowContext
        {
            public int FlowId { get; }
            public TcpClient? TcpClient { get; }
            public NetworkStream? LocalStream { get; }
            public UdpClient? UdpClient { get; }
            public IPEndPoint? UdpClientEndpoint { get; set; }
            private FlowDeliveryLane? _deliveryLane;
            public FlowDeliveryLane? DeliveryLane
            {
                get => Volatile.Read(ref _deliveryLane);
                set => Interlocked.Exchange(ref _deliveryLane, value);
            }
            public TaskCompletionSource<int> OpenResult { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            private FlowContext(int flowId, TcpClient? tcpClient, NetworkStream? stream, UdpClient? udpClient)
            {
                FlowId = flowId;
                TcpClient = tcpClient;
                LocalStream = stream;
                UdpClient = udpClient;
            }

            public static FlowContext ForTcp(int flowId, TcpClient client, NetworkStream stream) =>
                new(flowId, client, stream, null);

            public static FlowContext ForUdp(int flowId, TcpClient controlClient, NetworkStream controlStream, UdpClient udp) =>
                new(flowId, controlClient, controlStream, udp);

            public async Task<int> WaitOpenAsync(TimeSpan timeout, CancellationToken cancellationToken)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);
                try
                {
                    return await OpenResult.Task.WaitAsync(cts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return GatewayProtocol.Status.TtlExpired;
                }
            }

            public async Task CloseLocalAsync()
            {
                await DisposeLaneAsync();
                CloseSockets();
            }

            public async Task DisposeLaneAsync()
            {
                var lane = Interlocked.Exchange(ref _deliveryLane, null);
                if (lane != null)
                    await lane.DisposeAsync();
            }

            public void CloseSockets()
            {
                try { UdpClient?.Dispose(); } catch { }
                try { TcpClient?.Close(); } catch { }
            }
        }

        private sealed record SocksRequest(byte Command, string Host, int Port);
        private sealed record SocksUdpPacket(byte Fragment, string Host, int Port, byte[] Data);

        private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);
        public const int ListenPort = 8990;

        public readonly record struct SocksFlowMetrics(
            int ActiveTcp,
            int ActiveUdp,
            long TotalTcp,
            long TotalUdp);

        public async ValueTask DisposeAsync()
        {
            _gateway.FrameReceived -= HandleGatewayFrameAsync;
            _gateway.Disconnected -= HandleGatewayDisconnected;
            _gateway.FlowDispatchSnapshotProvider = null;
            await StopAsync();
        }
    }
}
