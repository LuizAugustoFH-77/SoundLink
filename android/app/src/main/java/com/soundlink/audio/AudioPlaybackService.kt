package com.soundlink.audio

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.util.Log

/**
 * Low-latency audio playback using AudioTrack.
 * Receives decoded PCM samples and plays them immediately.
 */
class AudioPlaybackService {
    companion object {
        private const val TAG = "AudioPlayback"
        const val SAMPLE_RATE = 48000
        const val CHANNELS = 2
        const val FRAME_SAMPLES_PER_CHANNEL = 480 // 10ms at 48kHz
        const val FRAME_SAMPLES = FRAME_SAMPLES_PER_CHANNEL * CHANNELS
    }

    private var audioTrack: AudioTrack? = null
    @Volatile
    private var volume: Float = 1.0f

    @Volatile
    var isPlaying = false
        private set

    fun start() {
        if (isPlaying) return

        val minBufferSize = AudioTrack.getMinBufferSize(
            SAMPLE_RATE,
            AudioFormat.CHANNEL_OUT_STEREO,
            AudioFormat.ENCODING_PCM_16BIT
        )

        // Use a small buffer for low latency — at least 2 frames worth
        val bufferSize = maxOf(minBufferSize, FRAME_SAMPLES * 2 * 2) // *2 for short→byte

        audioTrack = AudioTrack.Builder()
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                    .build()
            )
            .setAudioFormat(
                AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(SAMPLE_RATE)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                    .build()
            )
            .setBufferSizeInBytes(bufferSize)
            .setTransferMode(AudioTrack.MODE_STREAM)
            .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
            .build()

        audioTrack?.setVolume(volume)
        audioTrack?.play()
        isPlaying = true
        Log.i(TAG, "AudioTrack started (buffer=$bufferSize bytes)")
    }

    /**
     * Writes decoded PCM samples to the audio output.
     * @param samples PCM short array (interleaved stereo)
     * @param count number of samples to write
     */
    fun writeSamples(samples: ShortArray, count: Int) {
        if (!isPlaying || audioTrack == null) return
        audioTrack?.write(samples, 0, count)
    }

    fun setVolume(newVolume: Float) {
        volume = newVolume.coerceIn(0f, 1f)
        audioTrack?.setVolume(volume)
    }

    fun getVolume(): Float = volume

    fun stop() {
        if (!isPlaying) return
        isPlaying = false
        try {
            audioTrack?.stop()
            audioTrack?.release()
        } catch (e: Exception) {
            Log.w(TAG, "Error stopping AudioTrack", e)
        }
        audioTrack = null
        Log.i(TAG, "AudioTrack stopped")
    }
}
