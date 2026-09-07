package com.netrelay.connectivity.gateway

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.charset.StandardCharsets
import java.util.zip.CRC32

/**
 * AEL2 framed data-plane protocol used by NetRelay.
 *
 * The control channel (JSON on 8988) stays independent.
 * This protocol carries multiplexed TCP/UDP socket flows over one reliable byte stream.
 * It is intentionally versioned and bounded so a stale connection can be discarded
 * cleanly after reconnect instead of reusing half-dead flow state.
 */
object GatewayProtocol {
    const val MAGIC = 0x41454C32 // "AEL2"
    const val VERSION = 2
    const val HEADER_SIZE = 20
    const val MAX_PAYLOAD = 1024 * 1024

    object Type {
        const val HELLO = 1
        const val HELLO_ACK = 2

        const val TCP_CONNECT = 10
        const val TCP_DATA = 11
        const val TCP_CLOSE = 12
        const val UDP_OPEN = 13
        const val UDP_DATAGRAM = 14
        const val UDP_CLOSE = 15
        const val PING = 16

        const val TCP_CONNECT_RESULT = 20
        const val TCP_DATA_IN = 21
        const val FLOW_CLOSED = 22
        const val UDP_OPEN_RESULT = 23
        const val UDP_DATAGRAM_IN = 24
        const val PONG = 25
        const val ERROR = 30
    }

    object Status {
        const val OK = 0
        const val GENERAL_FAILURE = 1
        const val NETWORK_UNREACHABLE = 3
        const val HOST_UNREACHABLE = 4
        const val CONNECTION_REFUSED = 5
        const val TTL_EXPIRED = 6
        const val COMMAND_NOT_SUPPORTED = 7
        const val ADDRESS_NOT_SUPPORTED = 8
    }

    data class Frame(
        val type: Int,
        val flowId: Int = 0,
        val arg: Int = 0,
        val flags: Int = 0,
        val payload: ByteArray = ByteArray(0)
    ) {
        fun ownedCopy(): Frame = copy(payload = payload.copyOf())
    }

    data class DatagramPayload(
        val host: String,
        val port: Int,
        val data: ByteArray
    )

    fun readFrame(input: InputStream): Frame? {
        val data = if (input is DataInputStream) input else DataInputStream(input)
        val magic = try {
            data.readInt()
        } catch (_: EOFException) {
            return null
        }
        if (magic != MAGIC) {
            throw ProtocolException("Invalid gateway magic: 0x${magic.toUInt().toString(16)}")
        }

        val version = data.readUnsignedByte()
        if (version != VERSION) {
            throw ProtocolException("Unsupported gateway version: $version")
        }

        val type = data.readUnsignedByte()
        val flags = data.readUnsignedShort()
        val flowId = data.readInt()
        val arg = data.readInt()
        val payloadLength = data.readInt()
        if (payloadLength < 0 || payloadLength > MAX_PAYLOAD) {
            throw ProtocolException("Invalid payload length: $payloadLength")
        }

        val payload = ByteArray(payloadLength)
        data.readFully(payload)
        return Frame(type, flowId, arg, flags, payload)
    }

    fun writeFrame(output: OutputStream, frame: Frame) {
        val encoded = encodeFrame(frame)
        val data = if (output is DataOutputStream) output else DataOutputStream(output)
        data.write(encoded)
        data.flush()
    }

    fun encodeFrame(frame: Frame): ByteArray {
        val payload = frame.payload.copyOf()
        require(payload.size <= MAX_PAYLOAD) { "Gateway payload too large" }
        val buffer = ByteArrayOutputStream(HEADER_SIZE + payload.size)
        DataOutputStream(buffer).use { data ->
            data.writeInt(MAGIC)
            data.writeByte(VERSION)
            data.writeByte(frame.type)
            data.writeShort(frame.flags)
            data.writeInt(frame.flowId)
            data.writeInt(frame.arg)
            data.writeInt(payload.size)
            data.write(payload)
            data.flush()
        }
        return buffer.toByteArray()
    }

    fun crc32(bytes: ByteArray): String = CRC32().run {
        update(bytes)
        "%08X".format(value)
    }

    fun metadataHex(bytes: ByteArray, maximum: Int = 16): String =
        bytes.take(maximum).joinToString("") { "%02X".format(it) }

    fun encodeHost(host: String): ByteArray = host.toByteArray(StandardCharsets.UTF_8)

    fun decodeHost(payload: ByteArray): String = payload.toString(StandardCharsets.UTF_8)

    /**
     * UDP payload: [hostLen:u16][host:utf8][port:u16][datagram bytes...]
     */
    fun encodeDatagram(host: String, port: Int, data: ByteArray): ByteArray {
        val hostBytes = host.toByteArray(StandardCharsets.UTF_8)
        require(hostBytes.isNotEmpty() && hostBytes.size <= 65535) { "Invalid UDP host" }
        require(port in 1..65535) { "Invalid UDP port" }

        val buffer = ByteArrayOutputStream(hostBytes.size + data.size + 4)
        DataOutputStream(buffer).use { out ->
            out.writeShort(hostBytes.size)
            out.write(hostBytes)
            out.writeShort(port)
            out.write(data)
            out.flush()
        }
        return buffer.toByteArray()
    }

    fun decodeDatagram(payload: ByteArray): DatagramPayload {
        val input = DataInputStream(ByteArrayInputStream(payload))
        val hostLength = input.readUnsignedShort()
        if (hostLength <= 0 || hostLength > payload.size - 4) {
            throw ProtocolException("Invalid UDP host length: $hostLength")
        }
        val hostBytes = ByteArray(hostLength)
        input.readFully(hostBytes)
        val port = input.readUnsignedShort()
        if (port !in 1..65535) {
            throw ProtocolException("Invalid UDP port: $port")
        }
        val data = ByteArray(input.available())
        input.readFully(data)
        return DatagramPayload(hostBytes.toString(StandardCharsets.UTF_8), port, data)
    }
}

class ProtocolException(message: String, cause: Throwable? = null) : Exception(message, cause)
