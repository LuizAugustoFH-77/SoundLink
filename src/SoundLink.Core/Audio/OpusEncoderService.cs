using System;
using Concentus.Enums;
using Concentus.Structs;

namespace SoundLink.Core.Audio;

/// <summary>
/// Encodes PCM audio (48kHz, 16-bit, stereo) into Opus frames using Concentus.
/// </summary>
public sealed class OpusEncoderService : IDisposable
{
    /// <summary>Frame duration in milliseconds. 10ms = lowest latency for Opus.</summary>
    public const int FrameDurationMs = 10;

    /// <summary>Number of samples per frame per channel (48000 * 10/1000 = 480).</summary>
    public const int FrameSamplesPerChannel = AudioCaptureService.SampleRate * FrameDurationMs / 1000;

    /// <summary>Total samples per frame (stereo = 480 * 2 = 960).</summary>
    public const int FrameSamples = FrameSamplesPerChannel * AudioCaptureService.Channels;

    /// <summary>Bytes per frame of PCM input (960 samples * 2 bytes = 1920).</summary>
    public const int FrameBytes = FrameSamples * (AudioCaptureService.BitsPerSample / 8);

    /// <summary>Maximum Opus output frame size.</summary>
    private const int MaxOpusFrameSize = 4000;

    private readonly OpusEncoder _encoder;
    private int _bitrate;
    private bool _disposed;

    // Accumulation buffer for incomplete frames
    private readonly byte[] _accumBuffer;
    private int _accumOffset;

    public int Bitrate
    {
        get => _bitrate;
        set
        {
            _bitrate = Math.Clamp(value, 32000, 256000);
            _encoder.Bitrate = _bitrate;
        }
    }

    public OpusEncoderService(int bitrate = 128000)
    {
        _encoder = new OpusEncoder(
            AudioCaptureService.SampleRate,
            AudioCaptureService.Channels,
            OpusApplication.OPUS_APPLICATION_RESTRICTED_LOWDELAY);

        _bitrate = Math.Clamp(bitrate, 32000, 256000);
        _encoder.Bitrate = _bitrate;
        _encoder.ForceMode = OpusMode.MODE_CELT_ONLY; // lowest latency mode
        _encoder.SignalType = OpusSignal.OPUS_SIGNAL_MUSIC;

        _accumBuffer = new byte[FrameBytes];
        _accumOffset = 0;
    }

    /// <summary>
    /// Encodes a chunk of PCM data into Opus frames.
    /// Returns encoded frames via callback. Buffers incomplete frames internally.
    /// </summary>
    /// <param name="pcmData">Raw PCM bytes (48kHz, 16-bit, stereo).</param>
    /// <param name="pcmLength">Number of valid bytes in pcmData.</param>
    /// <param name="onFrame">Callback with (opusData, opusLength) for each encoded frame.</param>
    public void Encode(byte[] pcmData, int pcmLength, Action<byte[], int> onFrame)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OpusEncoderService));

        int srcOffset = 0;

        // First, fill any partial accumulated buffer
        if (_accumOffset > 0)
        {
            int needed = FrameBytes - _accumOffset;
            int toCopy = Math.Min(needed, pcmLength);
            Buffer.BlockCopy(pcmData, 0, _accumBuffer, _accumOffset, toCopy);
            _accumOffset += toCopy;
            srcOffset += toCopy;

            if (_accumOffset >= FrameBytes)
            {
                EncodeFrame(_accumBuffer, 0, onFrame);
                _accumOffset = 0;
            }
        }

        // Encode complete frames from input
        while (srcOffset + FrameBytes <= pcmLength)
        {
            EncodeFrame(pcmData, srcOffset, onFrame);
            srcOffset += FrameBytes;
        }

        // Save leftover bytes for next call
        int remaining = pcmLength - srcOffset;
        if (remaining > 0)
        {
            Buffer.BlockCopy(pcmData, srcOffset, _accumBuffer, _accumOffset, remaining);
            _accumOffset += remaining;
        }
    }

    private void EncodeFrame(byte[] pcm, int offset, Action<byte[], int> onFrame)
    {
        // Convert byte[] to short[] for Concentus
        var samples = new short[FrameSamples];
        for (int i = 0; i < FrameSamples; i++)
        {
            samples[i] = (short)(pcm[offset + i * 2] | (pcm[offset + i * 2 + 1] << 8));
        }

        var opusOutput = new byte[MaxOpusFrameSize];
        int encodedLength = _encoder.Encode(samples, 0, FrameSamplesPerChannel, opusOutput, 0, opusOutput.Length);

        if (encodedLength > 0)
        {
            onFrame(opusOutput, encodedLength);
        }
    }

    public void Reset()
    {
        _accumOffset = 0;
        _encoder.ResetState();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _encoder.ResetState();
    }
}
