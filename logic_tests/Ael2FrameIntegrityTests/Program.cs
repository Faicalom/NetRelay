using System.Buffers.Binary;
using System.Security.Cryptography;
using NetRelay.Services;

static void AssertPass(string name, bool condition, string details)
{
    if (!condition) throw new InvalidOperationException($"{name}=FAIL: {details}");
    Console.WriteLine($"{name}=PASS: {details}");
}

static byte[] Encode(params GatewayProtocol.Frame[] frames)
{
    using var stream = new MemoryStream();
    foreach (var frame in frames)
    {
        var encoded = GatewayProtocol.EncodeFrame(frame);
        stream.Write(encoded);
    }
    return stream.ToArray();
}

static GatewayProtocol.Frame? DecodeAndroidCompatible(Stream stream)
{
    var header = new byte[GatewayProtocol.HeaderSize];
    var first = stream.Read(header, 0, 1);
    if (first == 0) return null;
    ReadExactly(stream, header, 1, header.Length - 1);
    if (BinaryPrimitives.ReadUInt32BigEndian(header) != GatewayProtocol.Magic)
        throw new InvalidDataException("Android-compatible parser rejected magic");
    if (header[4] != GatewayProtocol.Version)
        throw new InvalidDataException("Android-compatible parser rejected version");
    var payloadLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
    if (payloadLength is < 0 or > GatewayProtocol.MaxPayload)
        throw new InvalidDataException("Android-compatible parser rejected payload length");
    var payload = new byte[payloadLength];
    ReadExactly(stream, payload, 0, payload.Length);
    return new GatewayProtocol.Frame(
        header[5],
        BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4)),
        BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12, 4)),
        BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6, 2)),
        payload);
}

static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
{
    while (count > 0)
    {
        var read = stream.Read(buffer, offset, count);
        if (read == 0) throw new EndOfStreamException();
        offset += read;
        count -= read;
    }
}

static bool Equal(GatewayProtocol.Frame expected, GatewayProtocol.Frame actual) =>
    expected.MessageType == actual.MessageType && expected.FlowId == actual.FlowId &&
    expected.Arg == actual.Arg && expected.Flags == actual.Flags &&
    expected.Data.SequenceEqual(actual.Data);

static List<GatewayProtocol.Frame> DecodeAll(Stream stream)
{
    var result = new List<GatewayProtocol.Frame>();
    while (true)
    {
        var frame = DecodeAndroidCompatible(stream);
        if (frame == null) return result;
        result.Add(frame);
    }
}

var udpBytes = new byte[] { 0x12, 0x34, 0x01, 0x00 };
var udpPayload = GatewayProtocol.EncodeDatagram("8.8.8.8", 53, udpBytes);
var udp = new GatewayProtocol.Frame(GatewayProtocol.Type.UdpDatagram, 278, Payload: udpPayload);

// TEST A: Windows encoder to an Android DataInputStream-compatible decoder.
using (var stream = new MemoryStream(Encode(udp)))
{
    var decoded = DecodeAndroidCompatible(stream)!;
    var datagram = GatewayProtocol.DecodeDatagram(decoded.Data);
    AssertPass("TEST_A_UDP_CODEC", Equal(udp, decoded) && datagram.Host == "8.8.8.8" &&
        datagram.Port == 53 && datagram.Data.SequenceEqual(udpBytes),
        "Windows encode and Android-compatible decode agree exactly.");
}

// TEST B: one byte per stream read.
using (var stream = new ChunkedReadStream(Encode(udp), _ => 1))
{
    AssertPass("TEST_B_BYTE_BY_BYTE", Equal(udp, DecodeAndroidCompatible(stream)!),
        "Partial headers and payloads decode with one byte per read.");
}

// TEST C: random chunk sizes from 1 through 37 bytes.
var random = new Random(0xA3E12);
using (var stream = new ChunkedReadStream(Encode(udp), remaining => Math.Min(remaining, random.Next(1, 38))))
{
    AssertPass("TEST_C_RANDOM_CHUNKS", Equal(udp, DecodeAndroidCompatible(stream)!),
        "Random stream segmentation preserves the frame.");
}

GatewayProtocol.Frame RandomFrame(Random rng, int index)
{
    byte[] types = [GatewayProtocol.Type.Ping, GatewayProtocol.Type.UdpDatagram,
        GatewayProtocol.Type.TcpData, GatewayProtocol.Type.Pong,
        GatewayProtocol.Type.UdpDatagram, GatewayProtocol.Type.FlowClosed];
    var type = types[index % types.Length];
    var payload = type == GatewayProtocol.Type.UdpDatagram
        ? GatewayProtocol.EncodeDatagram(index % 2 == 0 ? "1.1.1.1" : "dns.google", 53,
            Enumerable.Range(0, rng.Next(1, 300)).Select(_ => (byte)rng.Next(256)).ToArray())
        : Enumerable.Range(0, rng.Next(0, 300)).Select(_ => (byte)rng.Next(256)).ToArray();
    return new GatewayProtocol.Frame(type, index + 1, index * 17, Payload: payload);
}

// TEST D: 1000 concatenated mixed frames.
var mixedRandom = new Random(4145);
var thousand = Enumerable.Range(0, 1000).Select(i => RandomFrame(mixedRandom, i)).ToArray();
using (var stream = new ChunkedReadStream(Encode(thousand), remaining => Math.Min(remaining, mixedRandom.Next(1, 38))))
{
    var decoded = DecodeAll(stream);
    AssertPass("TEST_D_CONCATENATED_FRAMES", decoded.Count == thousand.Length &&
        thousand.Zip(decoded).All(pair => Equal(pair.First, pair.Second)),
        "1000 mixed concatenated frames decode without boundary drift.");
}

// TEST E: an immediately following AEL2 magic cannot become UDP hostLength.
var next = new GatewayProtocol.Frame(GatewayProtocol.Type.Ping, Arg: 44);
using (var stream = new ChunkedReadStream(Encode(udp, next), _ => 2))
{
    var first = DecodeAndroidCompatible(stream)!;
    var second = DecodeAndroidCompatible(stream)!;
    var datagram = GatewayProtocol.DecodeDatagram(first.Data);
    AssertPass("TEST_E_UDP_NEXT_FRAME_MAGIC", datagram.Host == "8.8.8.8" &&
        second.MessageType == GatewayProtocol.Type.Ping && stream.Position == stream.Length,
        "UDP decoding remains bounded to its payload before the next AEL2 magic.");
}

// TEST F: async ownership boundary retains byte-identical payload after source reuse.
var shared = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
var owned = new GatewayProtocol.Frame(GatewayProtocol.Type.TcpData, 9, Payload: shared).OwnedCopy();
var beforeHash = SHA256.HashData(owned.Data);
Array.Fill(shared, (byte)0xAE);
var afterHash = SHA256.HashData(owned.Data);
var projectRoot = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(projectRoot, "windows", "NetRelay.csproj")))
{
    projectRoot = Directory.GetParent(projectRoot)?.FullName
        ?? throw new DirectoryNotFoundException("Could not locate the NetRelay project root.");
}
var clientSource = File.ReadAllText(Path.Combine(projectRoot, "windows", "src", "Services", "NetRelayGatewayClient.cs"));
var writerSource = File.ReadAllText(Path.Combine(projectRoot, "windows", "src", "Services", "GatewayOutboundWriter.cs"));
AssertPass("TEST_F_BUFFER_OWNERSHIP", beforeHash.SequenceEqual(afterHash) &&
    clientSource.Contains("frame.OwnedCopy()") &&
    writerSource.Contains("var owned = frame.OwnedCopy();") &&
    writerSource.Contains("Encoded = GatewayProtocol.EncodeFrame(frame)"),
    "Queued/sent frames own payload bytes independently of reused source buffers.");

// TEST G: UDP and heartbeat frames remain intact under interleaving and delayed dispatch.
var interleaved = new List<GatewayProtocol.Frame>();
for (var i = 0; i < 2000; i++)
{
    interleaved.Add(RandomFrame(mixedRandom, i * 3));
    interleaved.Add(new GatewayProtocol.Frame(GatewayProtocol.Type.Ping, Arg: i));
    interleaved.Add(RandomFrame(mixedRandom, i * 3 + 1));
    interleaved.Add(new GatewayProtocol.Frame(GatewayProtocol.Type.Pong, Arg: i));
    interleaved.Add(new GatewayProtocol.Frame(GatewayProtocol.Type.TcpData, i, Payload: [(byte)i]));
    interleaved.Add(RandomFrame(mixedRandom, i * 3 + 2));
}
using (var stream = new ChunkedReadStream(Encode(interleaved.ToArray()), remaining => Math.Min(remaining, mixedRandom.Next(1, 38))))
{
    var decoded = DecodeAll(stream);
    AssertPass("TEST_G_HEARTBEAT_INTERLEAVE", decoded.Count == interleaved.Count &&
        interleaved.Zip(decoded).All(pair => Equal(pair.First, pair.Second)),
        "12,000 UDP/PING/PONG/TCP frames remain synchronized.");
}

// TEST H: both dispatchers carry or verify the active session generation.
var androidSource = File.ReadAllText(Path.Combine(projectRoot, "android", "app", "src", "main", "kotlin",
    "com", "netrelay", "connectivity", "gateway", "SocketGatewayServer.kt"));
AssertPass("TEST_H_SESSION_GENERATION",
    clientSource.Contains("QueuedFrame(long Generation") &&
    clientSource.Contains("stale queued frame dropped") &&
    androidSource.Contains("isCurrentGeneration()") && androidSource.Contains("stale frame dropped"),
    "Old-session queued work is rejected on Windows and Android.");

// TEST I: 10,000-frame stress with arbitrary stream segmentation.
var stressRandom = new Random(0x41454C32);
var stress = Enumerable.Range(0, 10_000).Select(i => RandomFrame(stressRandom, i)).ToArray();
using (var stream = new ChunkedReadStream(Encode(stress), remaining => Math.Min(remaining, stressRandom.Next(1, 38))))
{
    var decoded = DecodeAll(stream);
    AssertPass("TEST_I_10000_FRAME_STRESS", decoded.Count == stress.Length &&
        stress.Zip(decoded).All(pair => Equal(pair.First, pair.Second)),
        "10,000 frames pass without corruption or boundary loss.");
}

Console.WriteLine("AEL2_FRAME_INTEGRITY_TESTS=PASS");

sealed class ChunkedReadStream(byte[] bytes, Func<int, int> chunkSize) : MemoryStream(bytes)
{
    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = checked((int)(Length - Position));
        if (remaining == 0) return 0;
        return base.Read(buffer, offset, Math.Min(count, Math.Max(1, chunkSize(remaining))));
    }
}
