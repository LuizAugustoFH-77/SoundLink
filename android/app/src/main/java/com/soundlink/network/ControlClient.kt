package com.soundlink.network

import android.util.Log
import com.google.gson.Gson
import com.google.gson.JsonElement
import com.google.gson.annotations.SerializedName
import kotlinx.coroutines.*
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

/**
 * TCP+TLS client for the control channel.
 * Handles pairing, audio start/stop, volume, and latency measurement.
 */
class ControlClient {
    companion object {
        private const val TAG = "ControlClient"
        private const val CONNECT_TIMEOUT_MS = 5000
        private const val READ_TIMEOUT_MS = 0 // blocking
        const val TRANSPORT_UDP = "udp"
        const val TRANSPORT_TCP = "tcp"
    }

    private val gson = Gson()
    private var socket: Socket? = null
    private var sslSocket: SSLSocket? = null
    private var output: DataOutputStream? = null
    private var input: DataInputStream? = null
    private var readJob: Job? = null

    @Volatile
    var isConnected = false
        private set

    @Volatile
    var isPaired = false
        private set

    var onPairResult: ((Boolean, String, Int) -> Unit)? = null  // accepted, serverName, audioPort
    var onDisconnected: (() -> Unit)? = null
    var onLatencyMeasured: ((Long) -> Unit)? = null
    var onVolumeChanged: ((Float) -> Unit)? = null

    /**
     * Connects to the server via TLS.
     * Uses trust-all for self-signed certs (acceptable for local network pairing).
     */
    suspend fun connect(host: String, port: Int): Boolean = withContext(Dispatchers.IO) {
        try {
            // Trust all certs (self-signed server cert, local network only)
            val trustAllCerts = arrayOf<TrustManager>(object : X509TrustManager {
                override fun checkClientTrusted(chain: Array<java.security.cert.X509Certificate>?, authType: String?) {}
                override fun checkServerTrusted(chain: Array<java.security.cert.X509Certificate>?, authType: String?) {}
                override fun getAcceptedIssuers(): Array<java.security.cert.X509Certificate> = arrayOf()
            })

            val sslContext = SSLContext.getInstance("TLS")
            sslContext.init(null, trustAllCerts, java.security.SecureRandom())

            val rawSocket = Socket()
            rawSocket.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
            rawSocket.tcpNoDelay = true
            socket = rawSocket

            sslSocket = sslContext.socketFactory.createSocket(
                rawSocket, host, port, true
            ) as SSLSocket

            sslSocket!!.startHandshake()

            output = DataOutputStream(sslSocket!!.outputStream)
            input = DataInputStream(sslSocket!!.inputStream)

            isConnected = true
            Log.i(TAG, "TLS connection established to $host:$port")
            true
        } catch (e: Exception) {
            Log.e(TAG, "Connection failed", e)
            disconnect()
            false
        }
    }

    /**
     * Starts reading control messages in a coroutine.
     */
    fun startReading(scope: CoroutineScope) {
        readJob = scope.launch(Dispatchers.IO) {
            try {
                while (isActive && isConnected) {
                    val message = readMessage() ?: break
                    handleMessage(message)
                }
            } catch (e: Exception) {
                if (isConnected) Log.w(TAG, "Read error", e)
            } finally {
                if (isConnected) {
                    isConnected = false
                    isPaired = false
                    onDisconnected?.invoke()
                }
            }
        }
    }

    /**
     * Sends a pairing request with the PIN shown on the desktop app.
     */
    suspend fun sendPairRequest(deviceName: String, pin: String) {
        val msg = ControlMessage(
            type = "PairRequest",
            payload = gson.toJsonTree(PairRequestPayload(deviceName, pin))
        )
        sendMessage(msg)
    }

    suspend fun sendAudioStart(udpListenPort: Int, transport: String = TRANSPORT_UDP) {
        sendMessage(ControlMessage(
            type = "AudioStart",
            payload = gson.toJsonTree(AudioStartPayload(udpListenPort, transport))
        ))
    }

    suspend fun sendAudioStop() {
        sendMessage(ControlMessage(type = "AudioStop"))
    }

    suspend fun sendVolumeChange(volume: Float) {
        sendMessage(ControlMessage(
            type = "VolumeChange",
            payload = gson.toJsonTree(VolumeChangePayload(volume))
        ))
    }

    suspend fun sendDisconnect() {
        sendMessage(ControlMessage(type = "Disconnect"))
    }

    suspend fun sendPing() {
        val msg = ControlMessage(
            type = "Ping",
            payload = gson.toJsonTree(PingPongPayload(System.currentTimeMillis()))
        )
        sendMessage(msg)
    }

    fun disconnect() {
        isConnected = false
        isPaired = false
        readJob?.cancel()
        try { output?.close() } catch (_: Exception) {}
        try { input?.close() } catch (_: Exception) {}
        try { sslSocket?.close() } catch (_: Exception) {}
        try { socket?.close() } catch (_: Exception) {}
        output = null
        input = null
        sslSocket = null
        socket = null
    }

    private suspend fun sendMessage(msg: ControlMessage) = withContext(Dispatchers.IO) {
        try {
            val json = gson.toJson(msg)
            val bytes = json.toByteArray(Charsets.UTF_8)
            val lengthBytes = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(bytes.size).array()
            output?.write(lengthBytes)
            output?.write(bytes)
            output?.flush()
        } catch (e: Exception) {
            Log.e(TAG, "Send error", e)
        }
    }

    private fun readMessage(): ControlMessage? {
        val inp = input ?: return null
        // Read 4-byte length prefix (little-endian)
        val lengthBytes = ByteArray(4)
        inp.readFully(lengthBytes)
        val length = ByteBuffer.wrap(lengthBytes).order(ByteOrder.LITTLE_ENDIAN).int

        if (length <= 0 || length > 65536) return null

        val msgBytes = ByteArray(length)
        inp.readFully(msgBytes)
        val json = String(msgBytes, Charsets.UTF_8)
        return gson.fromJson(json, ControlMessage::class.java)
    }

    private fun handleMessage(msg: ControlMessage) {
        when (msg.type) {
            "PairAccepted" -> {
                isPaired = true
                val payload = gson.fromJson(msg.payload, PairResponsePayload::class.java)
                Log.i(TAG, "Paired with ${payload.serverName}")
                onPairResult?.invoke(true, payload.serverName, payload.audioPort)
            }
            "PairRejected" -> {
                Log.w(TAG, "Pairing rejected — wrong PIN")
                onPairResult?.invoke(false, "", 0)
            }
            "Pong" -> {
                val payload = gson.fromJson(msg.payload, PingPongPayload::class.java)
                val latency = System.currentTimeMillis() - payload.sendTime
                onLatencyMeasured?.invoke(latency)
            }
            "VolumeChange" -> {
                val payload = gson.fromJson(msg.payload, VolumeChangePayload::class.java)
                onVolumeChanged?.invoke(payload.volume)
            }
            "Disconnect" -> {
                disconnect()
                onDisconnected?.invoke()
            }
        }
    }
}

// -- Protocol data classes --

data class ControlMessage(
    @SerializedName("type") val type: String,
    @SerializedName("payload") val payload: JsonElement? = null,
    @SerializedName("timestamp") val timestamp: Long = System.currentTimeMillis()
)

data class PairRequestPayload(
    @SerializedName("deviceName") val deviceName: String,
    @SerializedName("pin") val pin: String
)

data class PairResponsePayload(
    @SerializedName("accepted") val accepted: Boolean = false,
    @SerializedName("serverName") val serverName: String = "",
    @SerializedName("audioPort") val audioPort: Int = 0
)

data class VolumeChangePayload(
    @SerializedName("volume") val volume: Float
)

data class PingPongPayload(
    @SerializedName("sendTime") val sendTime: Long
)

data class AudioStartPayload(
    @SerializedName("udpPort") val udpPort: Int,
    @SerializedName("transport") val transport: String = ControlClient.TRANSPORT_UDP
)
