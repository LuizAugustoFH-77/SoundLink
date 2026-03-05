package com.soundlink.service

import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Intent
import android.os.Binder
import android.os.IBinder
import android.util.Log
import com.soundlink.MainActivity
import com.soundlink.SoundLinkApp
import com.soundlink.audio.AudioPlaybackService
import com.soundlink.audio.JitterBuffer
import com.soundlink.audio.OpusDecoderJni
import com.soundlink.network.ControlClient
import com.soundlink.network.StreamReceiver
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Foreground service that manages the audio streaming session.
 * Keeps audio playing even when the screen is off.
 */
class AudioStreamService : Service() {
    companion object {
        private const val TAG = "AudioStreamService"
        private const val NOTIFICATION_ID = 1
    }

    inner class LocalBinder : Binder() {
        fun getService(): AudioStreamService = this@AudioStreamService
    }

    private val binder = LocalBinder()
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    val controlClient = ControlClient()
    private val jitterBuffer = JitterBuffer()
    private val streamReceiver = StreamReceiver(jitterBuffer)
    private val opusDecoder = OpusDecoderJni()
    private val audioPlayback = AudioPlaybackService()

    private var decoderJob: Job? = null
    private var pingJob: Job? = null
    private var isForegroundSession = false

    @Volatile
    var isStreaming = false
        private set

    @Volatile
    var currentServerName: String = ""
        private set

    @Volatile
    var latencyMs: Long = 0L
        private set

    @Volatile
    var playbackVolume: Float = 1.0f
        private set

    var onStateChanged: (() -> Unit)? = null

    override fun onBind(intent: Intent?): IBinder = binder

    override fun onCreate() {
        super.onCreate()
        opusDecoder.create()

        controlClient.onLatencyMeasured = { ms ->
            latencyMs = ms
            onStateChanged?.invoke()
        }

        controlClient.onDisconnected = {
            stopStreaming()
            onStateChanged?.invoke()
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == "STOP") {
            stopStreaming()
            stopSelf()
        }
        return START_NOT_STICKY
    }

    /**
     * Connects to a server, pairs, and starts audio streaming.
     */
    suspend fun connectAndStream(host: String, port: Int, pin: String, deviceName: String): Boolean {
        val audioTransport = if (isLoopbackHost(host)) ControlClient.TRANSPORT_TCP else ControlClient.TRANSPORT_UDP
        startOrUpdateForeground("Connecting to $host")

        if (!controlClient.connect(host, port)) {
            Log.e(TAG, "Failed to connect to $host:$port")
            stopForegroundSession()
            stopSelf()
            return false
        }

        controlClient.startReading(serviceScope)

        val pairResult = CompletableDeferred<Triple<Boolean, String, Int>>()
        controlClient.onPairResult = { accepted, serverName, audioPort ->
            if (!pairResult.isCompleted) {
                pairResult.complete(Triple(accepted, serverName, audioPort))
            }
        }

        controlClient.sendPairRequest(deviceName, pin)

        val (accepted, serverName, audioPort) = withTimeoutOrNull(10000) {
            pairResult.await()
        } ?: Triple(false, "", 0)

        if (!accepted) {
            Log.w(TAG, "Pairing rejected")
            controlClient.disconnect()
            stopForegroundSession()
            stopSelf()
            return false
        }

        currentServerName = serverName

        try {
            if (audioTransport == ControlClient.TRANSPORT_TCP) {
                streamReceiver.startTcp(host, audioPort, serviceScope)
                controlClient.sendAudioStart(0, ControlClient.TRANSPORT_TCP)
                Log.i(TAG, "Started TCP audio transport on $host:$audioPort")
            } else {
                streamReceiver.startUdp(0, serviceScope)
                val udpListenPort = streamReceiver.listenPort
                controlClient.sendAudioStart(udpListenPort, ControlClient.TRANSPORT_UDP)
                Log.i(TAG, "Started UDP audio transport on port $udpListenPort")
            }
        } catch (e: Exception) {
            Log.e(TAG, "Failed to initialize audio transport", e)
            controlClient.disconnect()
            stopForegroundSession()
            stopSelf()
            return false
        }

        audioPlayback.start()
        audioPlayback.setVolume(playbackVolume)

        isStreaming = true
        startDecoderLoop()
        startPingLoop()
        startOrUpdateForeground("Streaming from $currentServerName")

        onStateChanged?.invoke()
        Log.i(TAG, "Streaming from $serverName")
        return true
    }

    fun stopStreaming() {
        isStreaming = false
        decoderJob?.cancel()
        decoderJob = null
        pingJob?.cancel()
        pingJob = null
        streamReceiver.stop()
        audioPlayback.stop()
        jitterBuffer.reset()

        serviceScope.launch {
            try { controlClient.sendAudioStop() } catch (_: Exception) {}
            try { controlClient.sendDisconnect() } catch (_: Exception) {}
            controlClient.disconnect()
        }

        currentServerName = ""
        latencyMs = 0L
        stopForegroundSession()
        onStateChanged?.invoke()
    }

    fun setPlaybackVolume(volume: Float) {
        playbackVolume = volume.coerceIn(0f, 1f)
        audioPlayback.setVolume(playbackVolume)
        onStateChanged?.invoke()
    }

    private fun startDecoderLoop() {
        val pcmBuffer = ShortArray(AudioPlaybackService.FRAME_SAMPLES)

        decoderJob = serviceScope.launch(Dispatchers.Default) {
            while (isActive && isStreaming) {
                val frame = jitterBuffer.pop()
                if (frame != null) {
                    val decoded = opusDecoder.decode(
                        frame.opusData,
                        frame.opusLength,
                        pcmBuffer,
                        AudioPlaybackService.FRAME_SAMPLES_PER_CHANNEL
                    )
                    if (decoded > 0) {
                        audioPlayback.writeSamples(pcmBuffer, decoded * 2)
                    }
                } else {
                    delay(1)
                }
            }
        }
    }

    private fun startPingLoop() {
        pingJob?.cancel()
        pingJob = serviceScope.launch {
            while (isActive && isStreaming) {
                delay(2000)
                controlClient.sendPing()
            }
        }
    }

    private fun startOrUpdateForeground(contentText: String) {
        val notification = buildNotification(contentText)
        if (!isForegroundSession) {
            startForeground(NOTIFICATION_ID, notification)
            isForegroundSession = true
            return
        }

        val manager = getSystemService(NotificationManager::class.java)
        manager.notify(NOTIFICATION_ID, notification)
    }

    private fun stopForegroundSession() {
        if (!isForegroundSession) return
        stopForeground(STOP_FOREGROUND_REMOVE)
        isForegroundSession = false
    }

    private fun buildNotification(contentText: String): Notification {
        val stopIntent = Intent(this, AudioStreamService::class.java).apply { action = "STOP" }
        val stopPending = PendingIntent.getService(this, 0, stopIntent, PendingIntent.FLAG_IMMUTABLE)

        val openIntent = Intent(this, MainActivity::class.java)
        val openPending = PendingIntent.getActivity(this, 0, openIntent, PendingIntent.FLAG_IMMUTABLE)

        return Notification.Builder(this, SoundLinkApp.NOTIFICATION_CHANNEL_ID)
            .setContentTitle("SoundLink")
            .setContentText(contentText)
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setContentIntent(openPending)
            .addAction(Notification.Action.Builder(null, "Stop", stopPending).build())
            .setOngoing(true)
            .build()
    }

    private fun isLoopbackHost(host: String): Boolean {
        val normalized = host.trim().lowercase()
        return normalized == "127.0.0.1" || normalized == "localhost"
    }

    override fun onTaskRemoved(rootIntent: Intent?) {
        stopStreaming()
        stopSelf()
        super.onTaskRemoved(rootIntent)
    }

    override fun onDestroy() {
        stopStreaming()
        opusDecoder.destroy()
        serviceScope.cancel()
        super.onDestroy()
    }
}
