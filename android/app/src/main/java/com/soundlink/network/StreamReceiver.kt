package com.soundlink.network

import android.util.Log
import com.soundlink.audio.JitterBuffer
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.BufferedInputStream
import java.io.DataInputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * Receives audio packets over UDP (Wi-Fi) or TCP (USB/ADB) and feeds them into the jitter buffer.
 * Packet payload format is always [seq:u32 BE][timestamp:u32 BE][opus data...].
 * TCP wraps that packet with an outer [length:u32 BE] prefix.
 */
class StreamReceiver(
    private val jitterBuffer: JitterBuffer
) {
    companion object {
        private const val TAG = "StreamReceiver"
        private const val HEADER_SIZE = 8
        private const val MAX_PACKET_SIZE = 4096
        private const val CONNECT_TIMEOUT_MS = 5000
    }

    private var udpSocket: DatagramSocket? = null
    private var tcpSocket: Socket? = null
    private var receiveJob: Job? = null

    @Volatile
    var isReceiving = false
        private set

    var listenPort: Int = 0
        private set

    fun startUdp(port: Int, scope: CoroutineScope) {
        if (isReceiving) return

        udpSocket = DatagramSocket(port).apply {
            soTimeout = 0
            receiveBufferSize = 256 * 1024
        }
        listenPort = udpSocket!!.localPort
        isReceiving = true

        receiveJob = scope.launch(Dispatchers.IO) {
            val buffer = ByteArray(MAX_PACKET_SIZE)
            val packet = DatagramPacket(buffer, buffer.size)

            Log.i(TAG, "UDP receiver started on port $listenPort")

            while (isActive && isReceiving) {
                try {
                    udpSocket?.receive(packet)
                    handlePacket(buffer, packet.length)
                } catch (e: java.net.SocketTimeoutException) {
                    continue
                } catch (e: java.net.SocketException) {
                    if (isReceiving) Log.w(TAG, "UDP socket error", e)
                    break
                }
            }

            Log.i(TAG, "UDP receiver stopped")
        }
    }

    fun startTcp(host: String, port: Int, scope: CoroutineScope) {
        if (isReceiving) return

        tcpSocket = Socket().apply {
            tcpNoDelay = true
            connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
        }
        listenPort = 0
        isReceiving = true

        receiveJob = scope.launch(Dispatchers.IO) {
            val input = DataInputStream(BufferedInputStream(tcpSocket!!.getInputStream()))

            Log.i(TAG, "TCP receiver connected to $host:$port")

            while (isActive && isReceiving) {
                try {
                    val packetLength = input.readInt()
                    if (packetLength < HEADER_SIZE || packetLength > MAX_PACKET_SIZE) {
                        Log.w(TAG, "Invalid TCP audio packet length: $packetLength")
                        break
                    }

                    val packet = ByteArray(packetLength)
                    input.readFully(packet)
                    handlePacket(packet, packet.size)
                } catch (e: java.net.SocketException) {
                    if (isReceiving) Log.w(TAG, "TCP socket error", e)
                    break
                } catch (e: java.io.EOFException) {
                    break
                }
            }

            Log.i(TAG, "TCP receiver stopped")
        }
    }

    fun stop() {
        isReceiving = false
        receiveJob?.cancel()
        try { udpSocket?.close() } catch (_: Exception) {}
        try { tcpSocket?.close() } catch (_: Exception) {}
        udpSocket = null
        tcpSocket = null
        listenPort = 0
    }

    private fun handlePacket(buffer: ByteArray, packetLength: Int) {
        if (packetLength < HEADER_SIZE) return

        val wrapped = ByteBuffer.wrap(buffer, 0, HEADER_SIZE).order(ByteOrder.BIG_ENDIAN)
        val seqNum = wrapped.int.toLong() and 0xFFFFFFFFL
        val timestamp = wrapped.int.toLong() and 0xFFFFFFFFL

        val opusLength = packetLength - HEADER_SIZE
        if (opusLength <= 0) return

        jitterBuffer.push(
            sequenceNumber = seqNum,
            timestamp = timestamp,
            opusData = buffer.copyOfRange(HEADER_SIZE, HEADER_SIZE + opusLength),
            opusLength = opusLength
        )
    }
}
