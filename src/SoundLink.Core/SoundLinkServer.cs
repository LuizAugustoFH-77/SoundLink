using System;
using System.Threading;
using System.Threading.Tasks;
using SoundLink.Core.Audio;
using SoundLink.Core.Discovery;
using SoundLink.Core.Network;
using SoundLink.Core.Security;
using SoundLink.Core.Usb;

namespace SoundLink.Core;

/// <summary>
/// Main orchestrator that ties audio capture, encoding, networking, and discovery together.
/// This is the primary entry point for the desktop application's backend logic.
/// </summary>
public sealed class SoundLinkServer : IDisposable
{
    private readonly AudioCaptureService _audioCapture;
    private readonly OpusEncoderService _opusEncoder;
    private readonly NetworkServer _networkServer;
    private readonly MdnsAdvertiser _mdnsAdvertiser;
    private readonly TlsCertificateManager _certManager;
    private readonly AdbService _adbService;

    private CancellationTokenSource? _cts;
    private Task? _pingTask;
    private bool _disposed;
    private string? _audioDeviceId;
    private long _captureCount;
    private long _encodeCount;
    private long _sendCount;

    public AudioCaptureService AudioCapture => _audioCapture;
    public NetworkServer Network => _networkServer;
    public MdnsAdvertiser Discovery => _mdnsAdvertiser;
    public AdbService Usb => _adbService;
    public OpusEncoderService Encoder => _opusEncoder;

    public bool IsRunning => _networkServer.IsRunning;
    public string CurrentPin => _networkServer.CurrentPin;

    /// <summary>Aggregated log from all services.</summary>
    public event Action<string>? Log;

    public SoundLinkServer(int controlPort = NetworkServer.DefaultControlPort,
                           int audioPort = NetworkServer.DefaultAudioPort,
                           int bitrate = 128000)
    {
        _certManager = new TlsCertificateManager();
        _audioCapture = new AudioCaptureService();
        _opusEncoder = new OpusEncoderService(bitrate);
        _networkServer = new NetworkServer(_certManager, controlPort, audioPort);
        _mdnsAdvertiser = new MdnsAdvertiser();
        _adbService = new AdbService();

        // Wire up events
        _networkServer.Log += msg => Log?.Invoke(msg);
        _mdnsAdvertiser.Log += msg => Log?.Invoke(msg);
        _adbService.Log += msg => Log?.Invoke(msg);

        // When audio data is captured, encode and send
        _audioCapture.DataAvailable += OnAudioCaptured;

        // When a client connects, start capturing audio
        _networkServer.ClientConnected += OnClientConnected;
        _networkServer.ClientDisconnected += OnClientDisconnected;
    }

    /// <summary>
    /// Starts the server: network listener, mDNS advertisement, and optionally USB forwarding.
    /// Audio capture starts when a client connects and requests streaming.
    /// </summary>
    public async Task StartAsync(string? audioDeviceId = null, bool enableUsb = false, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SoundLinkServer));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _audioDeviceId = audioDeviceId;

        // Start network server
        await _networkServer.StartAsync(_cts.Token);

        // Start mDNS advertisement
        _mdnsAdvertiser.Start(_networkServer.ControlPort);

        // Optionally start USB forwarding
        if (enableUsb)
        {
            _adbService.StartForwarding(_networkServer.ControlPort, _networkServer.AudioPort);
        }

        // Start periodic ping for latency measurement
        _pingTask = PingLoopAsync(_cts.Token);

        Log?.Invoke($"[SoundLink] Server started. PIN: {CurrentPin}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _audioCapture.Stop();
        _opusEncoder.Reset();
        _mdnsAdvertiser.Stop();
        _adbService.StopForwarding();
        _networkServer.Stop();
        Log?.Invoke("[SoundLink] Server stopped.");
    }

    private void OnAudioCaptured(byte[] data, int length)
    {
        _captureCount++;
        if (_captureCount % 500 == 1)
        {
            Log?.Invoke($"[SoundLink] Audio captured #{_captureCount} ({length} bytes), IsStreaming={_networkServer.IsStreaming}");
        }

        if (!_networkServer.IsStreaming) return;

        _opusEncoder.Encode(data, length, (opusData, opusLength) =>
        {
            _encodeCount++;
            _networkServer.SendAudioFrame(opusData, opusLength);
            _sendCount++;
            if (_sendCount % 500 == 1)
            {
                Log?.Invoke($"[SoundLink] Sent frame #{_sendCount} ({opusLength} bytes)");
            }
        });
    }

    private void OnClientConnected(string deviceName)
    {
        Log?.Invoke($"[SoundLink] Client connected: {deviceName}");

        if (!_audioCapture.IsCapturing)
        {
            _audioCapture.Start(_audioDeviceId);
            Log?.Invoke("[SoundLink] Audio capture started.");
        }
    }

    private void OnClientDisconnected()
    {
        Log?.Invoke("[SoundLink] Client disconnected.");
        if (_networkServer.ConnectedClientCount == 0)
        {
            _audioCapture.Stop();
            _opusEncoder.Reset();
            Log?.Invoke("[SoundLink] Audio capture stopped (no active clients).");
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);
                if (_networkServer.IsPaired)
                {
                    await _networkServer.SendPingAsync();
                }
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _audioCapture.Dispose();
        _opusEncoder.Dispose();
        _networkServer.Dispose();
        _mdnsAdvertiser.Dispose();
        _adbService.Dispose();
    }
}
