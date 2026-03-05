using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace SoundLink.Core.Audio;

/// <summary>
/// Captures all system audio output using WASAPI loopback.
/// </summary>
public sealed class AudioCaptureService : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int BitsPerSample = 16;
    public static readonly WaveFormat CaptureFormat = new(SampleRate, BitsPerSample, Channels);

    private WasapiLoopbackCapture? _capture;
    private string? _deviceId;
    private volatile bool _isCapturing;

    /// <summary>
    /// Fired when a new chunk of PCM data (48kHz, 16-bit, stereo) is available.
    /// </summary>
    public event Action<byte[], int>? DataAvailable;

    /// <summary>
    /// Fired when capture stops (device removed, error, etc).
    /// </summary>
    public event Action<StoppedEventArgs>? CaptureStopped;

    public bool IsCapturing => _isCapturing;

    /// <summary>
    /// Lists all active audio render (output) devices.
    /// </summary>
    public static List<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        using var enumerator = new MMDeviceEnumerator();

        try
        {
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            devices.Add(new AudioDeviceInfo(defaultDevice.ID, $"{defaultDevice.FriendlyName} (Default)", true));
        }
        catch
        {
        }

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (devices.All(d => d.Id != device.ID))
            {
                devices.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, false));
            }
        }

        return devices;
    }

    /// <summary>
    /// Starts capturing audio from the specified device (or default if null).
    /// </summary>
    public void Start(string? deviceId = null)
    {
        if (_isCapturing) return;

        DisposeCapture();
        _deviceId = deviceId;

        if (deviceId != null)
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(deviceId);
            _capture = new WasapiLoopbackCapture(device);
        }
        else
        {
            _capture = new WasapiLoopbackCapture();
        }

        _capture.DataAvailable += OnRawDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
        _isCapturing = true;
    }

    public void Stop()
    {
        if (_capture == null) return;

        _isCapturing = false;
        try
        {
            _capture.StopRecording();
        }
        catch
        {
        }

        DisposeCapture();
    }

    private void OnRawDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _capture == null) return;

        var sourceFormat = _capture.WaveFormat;
        bool needsConversion = sourceFormat.SampleRate != SampleRate
                            || sourceFormat.BitsPerSample != BitsPerSample
                            || sourceFormat.Channels != Channels
                            || sourceFormat.Encoding != WaveFormatEncoding.Pcm;

        if (!needsConversion)
        {
            DataAvailable?.Invoke(e.Buffer, e.BytesRecorded);
            return;
        }

        try
        {
            using var inputStream = new RawSourceWaveStream(
                new MemoryStream(e.Buffer, 0, e.BytesRecorded, writable: false),
                sourceFormat);
            using var resampler = new MediaFoundationResampler(inputStream, CaptureFormat)
            {
                ResamplerQuality = 60
            };

            var estimatedBytes = EstimateConvertedLength(sourceFormat, e.BytesRecorded);
            var buffer = new byte[Math.Max(estimatedBytes, CaptureFormat.BlockAlign)];
            int bytesRead = resampler.Read(buffer, 0, buffer.Length);
            if (bytesRead > 0)
            {
                DataAvailable?.Invoke(buffer, bytesRead);
            }
        }
        catch
        {
            // Conversion failures should not tear down the capture pipeline.
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _isCapturing = false;
        CaptureStopped?.Invoke(e);
    }

    public void Dispose()
    {
        Stop();
        DisposeCapture();
    }

    private static int EstimateConvertedLength(WaveFormat sourceFormat, int sourceBytes)
    {
        if (sourceFormat.AverageBytesPerSecond <= 0)
        {
            return sourceBytes;
        }

        var durationSeconds = sourceBytes / (double)sourceFormat.AverageBytesPerSecond;
        var estimated = (int)Math.Ceiling(durationSeconds * CaptureFormat.AverageBytesPerSecond);
        return estimated + CaptureFormat.BlockAlign;
    }

    private void DisposeCapture()
    {
        if (_capture == null) return;

        _capture.DataAvailable -= OnRawDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
        _capture = null;
    }
}

public record AudioDeviceInfo(string Id, string Name, bool IsDefault);
