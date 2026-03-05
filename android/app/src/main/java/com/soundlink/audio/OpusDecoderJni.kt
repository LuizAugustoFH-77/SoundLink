package com.soundlink.audio

/**
 * JNI bridge to native libopus decoder.
 */
class OpusDecoderJni {
    private var handle: Long = 0

    fun create(sampleRate: Int = 48000, channels: Int = 2): Boolean {
        handle = nativeCreate(sampleRate, channels)
        return handle != 0L
    }

    /**
     * Decodes an Opus frame into PCM samples.
     * @return number of decoded samples per channel, or negative on error
     */
    fun decode(opusData: ByteArray, opusLength: Int, pcmOutput: ShortArray, frameSamplesPerChannel: Int): Int {
        if (handle == 0L) return -1
        return nativeDecode(handle, opusData, opusLength, pcmOutput, frameSamplesPerChannel)
    }

    fun reset() {
        if (handle != 0L) nativeReset(handle)
    }

    fun destroy() {
        if (handle != 0L) {
            nativeDestroy(handle)
            handle = 0
        }
    }

    private external fun nativeCreate(sampleRate: Int, channels: Int): Long
    private external fun nativeDecode(handle: Long, opusData: ByteArray, opusLength: Int, pcmOutput: ShortArray, frameSamplesPerChannel: Int): Int
    private external fun nativeReset(handle: Long)
    private external fun nativeDestroy(handle: Long)

    companion object {
        init {
            System.loadLibrary("soundlink_opus")
        }
    }
}
