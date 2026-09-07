using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetRelay.Services
{
    public static class GatewayProtocol
    {
        public const uint Magic = 0x41454C32; // AEL2
        public const byte Version = 2;
        public const int HeaderSize = 20;
        public const int MaxPayload = 1024 * 1024;

        public static class Type
        {
            public const byte Hello = 1;
            public const byte HelloAck = 2;
            public const byte TcpConnect = 10;
            public const byte TcpData = 11;
            public const byte TcpClose = 12;
            public const byte UdpOpen = 13;
            public const byte UdpDatagram = 14;
            public const byte UdpClose = 15;
            public const byte Ping = 16;
            public const byte TcpConnectResult = 20;
            public const byte TcpDataIn = 21;
            public const byte FlowClosed = 22;
            public const byte UdpOpenResult = 23;
            public const byte UdpDatagramIn = 24;
            public const byte Pong = 25;
            public const byte Error = 30;
        }

        public static class Status
        {
            public const int Ok = 0;
            public const int GeneralFailure = 1;
            public const int NetworkUnreachable = 3;
            public const int HostUnreachable = 4;
            public const int ConnectionRefused = 5;
            public const int TtlExpired = 6;
            public const int CommandNotSupported = 7;
            public const int AddressNotSupported = 8;
        }

        public sealed record Frame(
            byte MessageType,
            int FlowId = 0,
            int Arg = 0,
            ushort Flags = 0,
            byte[]? Payload = null)
        {
            public byte[] Data => Payload ?? Array.Empty<byte>();

            public Frame OwnedCopy() => this with { Payload = Data.AsSpan().ToArray() };
        }

        public sealed record DatagramPayload(string Host, int Port, byte[] Data);

        public static async Task<Frame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
        {
            var header = new byte[HeaderSize];
            var first = await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken);
            if (first == 0)
            {
                return null;
            }

            await ReadExactlyAsync(stream, header.AsMemory(1), cancellationToken);
            var magic = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            if (magic != Magic)
            {
                throw new InvalidDataException($"Invalid gateway magic 0x{magic:X8}");
            }
            if (header[4] != Version)
            {
                throw new InvalidDataException($"Unsupported gateway version {header[4]}");
            }

            var messageType = header[5];
            var flags = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(6, 2));
            var flowId = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4));
            var arg = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12, 4));
            var payloadLength = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
            if (payloadLength < 0 || payloadLength > MaxPayload)
            {
                throw new InvalidDataException($"Invalid gateway payload length {payloadLength}");
            }

            var payload = payloadLength == 0 ? Array.Empty<byte>() : new byte[payloadLength];
            if (payloadLength > 0)
            {
                await ReadExactlyAsync(stream, payload, cancellationToken);
            }

            return new Frame(messageType, flowId, arg, flags, payload);
        }

        public static async Task WriteFrameAsync(Stream stream, Frame frame, CancellationToken cancellationToken)
        {
            var encoded = EncodeFrame(frame);
            await WriteEncodedFrameAsync(stream, encoded, flush: true, cancellationToken);
        }

        public static async Task WriteEncodedFrameAsync(
            Stream stream,
            ReadOnlyMemory<byte> encoded,
            bool flush,
            CancellationToken cancellationToken)
        {
            await stream.WriteAsync(encoded, cancellationToken);
            if (flush)
                await stream.FlushAsync(cancellationToken);
        }

        public static byte[] EncodeFrame(Frame frame)
        {
            // Capture an owned payload before any async boundary. A Frame can be queued
            // or its caller-owned byte[] can otherwise outlive the initiating call.
            var payload = frame.Data.AsSpan().ToArray();
            if (payload.Length > MaxPayload)
            {
                throw new InvalidDataException("Gateway payload too large");
            }

            var encoded = new byte[HeaderSize + payload.Length];
            BinaryPrimitives.WriteUInt32BigEndian(encoded.AsSpan(0, 4), Magic);
            encoded[4] = Version;
            encoded[5] = frame.MessageType;
            BinaryPrimitives.WriteUInt16BigEndian(encoded.AsSpan(6, 2), frame.Flags);
            BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(8, 4), frame.FlowId);
            BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(12, 4), frame.Arg);
            BinaryPrimitives.WriteInt32BigEndian(encoded.AsSpan(16, 4), payload.Length);
            payload.CopyTo(encoded.AsSpan(HeaderSize));
            return encoded;
        }

        public static uint Crc32(ReadOnlySpan<byte> bytes)
        {
            uint crc = 0xFFFFFFFF;
            foreach (var value in bytes)
            {
                crc ^= value;
                for (var bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
            return ~crc;
        }

        public static byte[] EncodeHost(string host) => Encoding.UTF8.GetBytes(host);
        public static string DecodeHost(byte[] payload) => Encoding.UTF8.GetString(payload);

        public static byte[] EncodeDatagram(string host, int port, ReadOnlySpan<byte> data)
        {
            var hostBytes = Encoding.UTF8.GetBytes(host);
            if (hostBytes.Length == 0 || hostBytes.Length > ushort.MaxValue)
                throw new InvalidDataException("Invalid UDP host");
            if (port is < 1 or > 65535)
                throw new InvalidDataException("Invalid UDP port");

            var payload = new byte[2 + hostBytes.Length + 2 + data.Length];
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(0, 2), (ushort)hostBytes.Length);
            hostBytes.CopyTo(payload.AsSpan(2));
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(2 + hostBytes.Length, 2), (ushort)port);
            data.CopyTo(payload.AsSpan(4 + hostBytes.Length));
            return payload;
        }

        public static DatagramPayload DecodeDatagram(byte[] payload)
        {
            if (payload.Length < 4)
                throw new InvalidDataException("Short UDP gateway payload");
            var hostLength = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0, 2));
            if (hostLength == 0 || 4 + hostLength > payload.Length)
                throw new InvalidDataException("Invalid UDP host length");
            var host = Encoding.UTF8.GetString(payload, 2, hostLength);
            var port = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2 + hostLength, 2));
            var dataOffset = 4 + hostLength;
            var data = payload.AsSpan(dataOffset).ToArray();
            return new DatagramPayload(host, port, data);
        }

        private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer[offset..], cancellationToken);
                if (count == 0)
                    throw new EndOfStreamException();
                offset += count;
            }
        }
    }
}
