#include <jni.h>
#include <opus.h>
#include <android/log.h>
#include <cstring>

#define LOG_TAG "SoundLinkOpus"
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, LOG_TAG, __VA_ARGS__)

// Store decoder pointer as a long in Java
static OpusDecoder* getDecoder(jlong handle) {
    return reinterpret_cast<OpusDecoder*>(handle);
}

extern "C" {

JNIEXPORT jlong JNICALL
Java_com_soundlink_audio_OpusDecoderJni_nativeCreate(
        JNIEnv* env, jobject /* this */,
        jint sampleRate, jint channels) {
    int error = 0;
    OpusDecoder* decoder = opus_decoder_create(sampleRate, channels, &error);
    if (error != OPUS_OK || decoder == nullptr) {
        LOGE("Failed to create Opus decoder: %s", opus_strerror(error));
        return 0;
    }
    return reinterpret_cast<jlong>(decoder);
}

JNIEXPORT jint JNICALL
Java_com_soundlink_audio_OpusDecoderJni_nativeDecode(
        JNIEnv* env, jobject /* this */,
        jlong handle,
        jbyteArray opusData, jint opusLength,
        jshortArray pcmOutput, jint frameSamplesPerChannel) {
    auto* decoder = getDecoder(handle);
    if (!decoder) return -1;

    jbyte* opus = env->GetByteArrayElements(opusData, nullptr);
    jshort* pcm = env->GetShortArrayElements(pcmOutput, nullptr);

    int decoded = opus_decode(
            decoder,
            reinterpret_cast<const unsigned char*>(opus), opusLength,
            pcm, frameSamplesPerChannel,
            0 /* no FEC */);

    env->ReleaseByteArrayElements(opusData, opus, JNI_ABORT);
    env->ReleaseShortArrayElements(pcmOutput, pcm, 0);

    if (decoded < 0) {
        LOGE("Opus decode error: %s", opus_strerror(decoded));
    }
    return decoded;
}

JNIEXPORT void JNICALL
Java_com_soundlink_audio_OpusDecoderJni_nativeReset(
        JNIEnv* env, jobject /* this */, jlong handle) {
    auto* decoder = getDecoder(handle);
    if (decoder) {
        opus_decoder_ctl(decoder, OPUS_RESET_STATE);
    }
}

JNIEXPORT void JNICALL
Java_com_soundlink_audio_OpusDecoderJni_nativeDestroy(
        JNIEnv* env, jobject /* this */, jlong handle) {
    auto* decoder = getDecoder(handle);
    if (decoder) {
        opus_decoder_destroy(decoder);
    }
}

} // extern "C"
