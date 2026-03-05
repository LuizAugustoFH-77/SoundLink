using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using SoundLink.Core.Security;

namespace SoundLink.Core.Network;

/// <summary>
/// Manages TCP+TLS control connections plus UDP or TCP audio streaming.
/// UDP is used on the local network; TCP audio is available for USB/ADB forwarding.
/// </summary>
public sealed class NetworkServer : IDisposable
{
    public const int DefaultControlPort = 7359;
    public const int DefaultAudioPort = 7360;

    private sealed class ClientSession : IDisposable
    {
        public Guid Id { get; } = Guid.NewGuid();
        public TcpClient ControlTcpClient { get; }
        public SslStream ControlSslStream { get; }
        public IPAddress RemoteIp { get; }
        public object AudioTcpSync { get; } = new();
        public SemaphoreSlim ControlWriteLock { get; } = new(1, 1);

        public string DeviceName { get; set; } = "";
        public string AudioTransport { get; set; } = SoundLink.Core.Network.AudioTransport.Udp;
        public IPEndPoint? UdpEndpoint { get; set; }
        public TcpClient? AudioTcpClient { get; set; }
        public NetworkStream? AudioTcpStream { get; set; }
        public bool IsPaired { get; set; }
        public bool IsStreaming { get; set; }
        public long LastLatencyMs { get; set; }

        public ClientSession(TcpClient controlTcpClient, SslStream controlSslStream)
        {
            ControlTcpClient = controlTcpClient;
            ControlSslStream = controlSslStream;
            RemoteIp = ((IPEndPoint)controlTcpClient.Client.RemoteEndPoint!).Address;
        }

        public void Dispose()
        {
            try { AudioTcpStream?.Close(); } catch { }
            try { AudioTcpClient?.Close(); } catch { }
            try { ControlSslStream.Close(); } catch { }
            try { ControlTcpClient.Close(); } catch { }
            ControlWriteLock.Dispose();
        }
    }

    private readonly TlsCertificateManager _certManager;
    private readonly object _sessionSync = new();

    private TcpListener? _tcpListener;
    private TcpListener? _audioTcpListener;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<Guid, ClientSession> _sessions = new();

    private string _currentPin = "";
    private uint _sequenceNumber;

    public int ControlPort { get; }
    public int AudioPort { get; }
    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
    public bool IsPaired
    {
        get
        {
            lock (_sessionSync)
            {
                return _sessions.Values.Any(session => session.IsPaired);
            }
        }
    }
    public bool IsStreaming
    {
        get
        {
            lock (_sessionSync)
            {
                return _sessions.Values.Any(session => session.IsStreaming);
            }
        }
    }
    public int ConnectedClientCount
    {
        get
        {
            lock (_sessionSync)
            {
                return _sessions.Values.Count(session => session.IsPaired);
            }
        }
    }
    public int StreamingClientCount
    {
        get
        {
            lock (_sessionSync)
            {
                return _sessions.Values.Count(session => session.IsStreaming);
            }
        }
    }
    public string ConnectedClientSummary
    {
        get
        {
            var names = GetConnectedDeviceNames();
            return names.Count switch
            {
                0 => "",
                1 => names[0],
                2 => $"{names[0]} + 1 more",
                _ => $"{names.Count} devices"
            };
        }
    }
    public string CurrentPin => _currentPin;

    public event Action<string>? ClientConnected;    // device name
    public event Action? ClientDisconnected;
    public event Action? ClientsChanged;
    public event Action<float>? VolumeChangeRequested; // 0.0-1.0
    public event Action<long>? LatencyMeasured;       // average ms
    public event Action<string>? Log;

    public NetworkServer(TlsCertificateManager certManager, int controlPort = DefaultControlPort, int audioPort = DefaultAudioPort)
    {
        _certManager = certManager;
        ControlPort = controlPort;
        AudioPort = audioPort;
    }

    /// <summary>
    /// Starts listening for control and audio connections.
    /// </summary>
    public async Task StartAsync(CancellationToken externalToken = default)
    {
        if (IsRunning) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        _currentPin = TlsCertificateManager.GeneratePairingPin();
        _sequenceNumber = 0;

        var cert = _certManager.GetOrCreateCertificate();

        _tcpListener = new TcpListener(IPAddress.Any, ControlPort);
        _tcpListener.Start();
        LogMessage($"TCP control server listening on port {ControlPort}");

        _udpClient = new UdpClient(AudioPort);
        LogMessage($"UDP audio server listening on port {AudioPort}");

        _audioTcpListener = new TcpListener(IPAddress.Any, AudioPort);
        _audioTcpListener.Start();
        LogMessage($"TCP audio server listening on port {AudioPort}");

        _ = AcceptConnectionsAsync(cert, _cts.Token);
        _ = AcceptAudioConnectionsAsync(_cts.Token);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Sends an Opus audio frame to every actively streaming client.
    /// </summary>
    public void SendAudioFrame(byte[] opusData, int opusLength)
    {
        List<ClientSession> streamingSessions;
        lock (_sessionSync)
        {
            streamingSessions = _sessions.Values.Where(session => session.IsStreaming).ToList();
        }

        if (streamingSessions.Count == 0) return;

        var seq = Interlocked.Increment(ref _sequenceNumber);
        var timestamp = (uint)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0xFFFFFFFF);
        var packet = AudioPacket.Create((uint)seq, timestamp, opusData, opusLength);

        foreach (var session in streamingSessions)
        {
            if (string.Equals(session.AudioTransport, AudioTransport.Tcp, StringComparison.OrdinalIgnoreCase))
            {
                SendAudioFrameOverTcp(session, packet);
            }
            else
            {
                SendAudioFrameOverUdp(session, packet);
            }
        }
    }

    /// <summary>
    /// Regenerates the pairing PIN for future connections.
    /// Existing paired clients remain connected.
    /// </summary>
    public string RegeneratePin()
    {
        _currentPin = TlsCertificateManager.GeneratePairingPin();
        return _currentPin;
    }

    public void Stop()
    {
        _cts?.Cancel();

        List<ClientSession> sessions;
        lock (_sessionSync)
        {
            sessions = _sessions.Values.ToList();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            session.Dispose();
        }

        _tcpListener?.Stop();
        _audioTcpListener?.Stop();
        _udpClient?.Close();
        _tcpListener = null;
        _audioTcpListener = null;
        _udpClient = null;
        _cts = null;
        LogMessage("Server stopped");
    }

    public void Dispose() => Stop();

    public IReadOnlyList<string> GetConnectedDeviceNames()
    {
        lock (_sessionSync)
        {
            return _sessions.Values
                .Where(session => session.IsPaired)
                .Select(session => session.DeviceName)
                .OrderBy(name => name)
                .ToList();
        }
    }

    private async Task AcceptConnectionsAsync(X509Certificate2 cert, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tcpClient = await _tcpListener!.AcceptTcpClientAsync(ct);
                LogMessage($"TCP connection from {tcpClient.Client.RemoteEndPoint}");
                _ = HandleClientAsync(tcpClient, cert, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                LogMessage($"Accept error: {ex.Message}");
            }
        }
    }

    private async Task AcceptAudioConnectionsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var audioClient = await _audioTcpListener!.AcceptTcpClientAsync(ct);
                audioClient.NoDelay = true;

                var remoteIp = ((IPEndPoint)audioClient.Client.RemoteEndPoint!).Address;
                var session = TryAssignAudioTcpClient(remoteIp, audioClient);
                if (session == null)
                {
                    LogMessage($"Rejected TCP audio connection from {audioClient.Client.RemoteEndPoint}");
                    audioClient.Close();
                    continue;
                }

                LogMessage($"TCP audio connection from {audioClient.Client.RemoteEndPoint} for {session.DeviceName}");
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                LogMessage($"Audio accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, X509Certificate2 cert, CancellationToken ct)
    {
        SslStream? sslStream = null;
        ClientSession? session = null;

        try
        {
            tcpClient.NoDelay = true;
            sslStream = new SslStream(tcpClient.GetStream(), false);

            await sslStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = cert,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, ct);

            LogMessage("TLS handshake completed");

            session = new ClientSession(tcpClient, sslStream);
            lock (_sessionSync)
            {
                _sessions[session.Id] = session;
            }

            await ProcessControlMessagesAsync(session, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            LogMessage($"Client error: {ex.Message}");
        }
        finally
        {
            if (session != null)
            {
                RemoveSession(session);
            }
            else
            {
                sslStream?.Dispose();
                tcpClient.Close();
            }
        }
    }

    private async Task ProcessControlMessagesAsync(ClientSession session, CancellationToken ct)
    {
        var lengthBuffer = new byte[4];

        while (!ct.IsCancellationRequested && session.ControlTcpClient.Connected)
        {
            int totalRead = 0;
            while (totalRead < 4)
            {
                int read = await session.ControlSslStream.ReadAsync(lengthBuffer.AsMemory(totalRead, 4 - totalRead), ct);
                if (read == 0) return;
                totalRead += read;
            }

            int messageLength = BitConverter.ToInt32(lengthBuffer, 0);
            if (messageLength <= 0 || messageLength > 1024 * 64)
            {
                LogMessage($"Invalid message length from {session.RemoteIp}: {messageLength}");
                return;
            }

            var messageBuffer = new byte[messageLength];
            totalRead = 0;
            while (totalRead < messageLength)
            {
                int read = await session.ControlSslStream.ReadAsync(messageBuffer.AsMemory(totalRead, messageLength - totalRead), ct);
                if (read == 0) return;
                totalRead += read;
            }

            var message = ControlMessage.Deserialize(messageBuffer, 0, messageLength);
            if (message == null) continue;

            await HandleMessageAsync(session, message, ct);
        }
    }

    private async Task HandleMessageAsync(ClientSession session, ControlMessage message, CancellationToken ct)
    {
        switch (message.Type)
        {
            case MessageType.PairRequest:
                await HandlePairRequest(session, message, ct);
                break;

            case MessageType.AudioStart:
                StartStreaming(session, message);
                break;

            case MessageType.AudioStop:
                session.IsStreaming = false;
                CloseAudioTcpConnection(session);
                LogMessage($"Audio streaming stopped by {session.DeviceName}");
                NotifyClientsChanged();
                break;

            case MessageType.VolumeChange:
                var vol = message.GetPayload<VolumeChangePayload>();
                if (vol != null) VolumeChangeRequested?.Invoke(vol.Volume);
                break;

            case MessageType.Ping:
                var ping = message.GetPayload<PingPongPayload>();
                var pong = ControlMessage.Create(MessageType.Pong, new PingPongPayload
                {
                    SendTime = ping?.SendTime ?? 0
                });
                await SendControlMessageAsync(session, pong, ct);
                break;

            case MessageType.Pong:
                var pongPayload = message.GetPayload<PingPongPayload>();
                if (pongPayload != null)
                {
                    session.LastLatencyMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pongPayload.SendTime;
                    LatencyMeasured?.Invoke(CalculateAverageLatency());
                }
                break;

            case MessageType.Disconnect:
                RemoveSession(session);
                break;
        }
    }

    private async Task HandlePairRequest(ClientSession session, ControlMessage message, CancellationToken ct)
    {
        var request = message.GetPayload<PairRequestPayload>();
        if (request == null) return;

        bool pinValid = request.Pin == _currentPin;
        LogMessage($"Pair request from '{request.DeviceName}' - PIN {(pinValid ? "valid" : "invalid")}");

        if (!pinValid)
        {
            var rejected = ControlMessage.Create(MessageType.PairRejected, new PairResponsePayload
            {
                Accepted = false,
                ServerName = Environment.MachineName,
                AudioPort = 0
            });
            await SendControlMessageAsync(session, rejected, ct);
            return;
        }

        session.DeviceName = request.DeviceName;
        session.IsPaired = true;

        var accepted = ControlMessage.Create(MessageType.PairAccepted, new PairResponsePayload
        {
            Accepted = true,
            ServerName = Environment.MachineName,
            AudioPort = AudioPort
        });
        await SendControlMessageAsync(session, accepted, ct);

        ClientConnected?.Invoke(request.DeviceName);
        NotifyClientsChanged();
    }

    private void StartStreaming(ClientSession session, ControlMessage message)
    {
        if (!session.IsPaired) return;

        var payload = message.GetPayload<AudioStartPayload>();
        session.AudioTransport = payload?.Transport ?? AudioTransport.Udp;
        session.IsStreaming = true;

        if (string.Equals(session.AudioTransport, AudioTransport.Tcp, StringComparison.OrdinalIgnoreCase))
        {
            session.UdpEndpoint = null;
            LogMessage($"Audio streaming started over TCP for {session.DeviceName}");
            NotifyClientsChanged();
            return;
        }

        var clientUdpPort = payload?.UdpPort ?? AudioPort;
        session.UdpEndpoint = new IPEndPoint(session.RemoteIp, clientUdpPort);
        LogMessage($"Audio streaming started to {session.UdpEndpoint} for {session.DeviceName}");
        NotifyClientsChanged();
    }

    private void RemoveSession(ClientSession session)
    {
        bool removed;
        bool wasPaired;
        string deviceName;

        lock (_sessionSync)
        {
            removed = _sessions.Remove(session.Id);
            wasPaired = session.IsPaired;
            deviceName = session.DeviceName;
        }

        if (!removed) return;

        session.Dispose();

        if (wasPaired)
        {
            ClientDisconnected?.Invoke();
            LogMessage($"Client disconnected: {deviceName}");
            NotifyClientsChanged();
            LatencyMeasured?.Invoke(CalculateAverageLatency());
        }
    }

    private void SendAudioFrameOverUdp(ClientSession session, byte[] packet)
    {
        if (_udpClient == null || session.UdpEndpoint == null) return;

        try
        {
            _udpClient.Send(packet, packet.Length, session.UdpEndpoint);
        }
        catch (SocketException)
        {
        }
    }

    private void SendAudioFrameOverTcp(ClientSession session, byte[] packet)
    {
        var stream = session.AudioTcpStream;
        var client = session.AudioTcpClient;
        if (stream == null || client == null || !client.Connected) return;

        var lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, packet.Length);

        try
        {
            lock (session.AudioTcpSync)
            {
                stream.Write(lengthPrefix, 0, lengthPrefix.Length);
                stream.Write(packet, 0, packet.Length);
                stream.Flush();
            }
        }
        catch (Exception ex)
        {
            LogMessage($"TCP audio send error for {session.DeviceName}: {ex.Message}");
            RemoveSession(session);
        }
    }

    private ClientSession? TryAssignAudioTcpClient(IPAddress remoteIp, TcpClient audioClient)
    {
        lock (_sessionSync)
        {
            var session = _sessions.Values
                .Where(candidate => candidate.IsPaired && Equals(candidate.RemoteIp, remoteIp))
                .OrderBy(candidate => candidate.AudioTcpClient != null && candidate.AudioTcpClient.Connected ? 1 : 0)
                .FirstOrDefault();

            if (session == null)
            {
                return null;
            }

            try { session.AudioTcpStream?.Close(); } catch { }
            try { session.AudioTcpClient?.Close(); } catch { }

            session.AudioTcpClient = audioClient;
            session.AudioTcpStream = audioClient.GetStream();
            return session;
        }
    }

    private void CloseAudioTcpConnection(ClientSession session)
    {
        try { session.AudioTcpStream?.Close(); } catch { }
        try { session.AudioTcpClient?.Close(); } catch { }
        session.AudioTcpStream = null;
        session.AudioTcpClient = null;
    }

    private async Task SendControlMessageAsync(ClientSession session, ControlMessage message, CancellationToken ct)
    {
        var data = message.Serialize();
        await session.ControlWriteLock.WaitAsync(ct);
        try
        {
            await session.ControlSslStream.WriteAsync(data, ct);
            await session.ControlSslStream.FlushAsync(ct);
        }
        finally
        {
            session.ControlWriteLock.Release();
        }
    }

    /// <summary>
    /// Sends a ping to every paired client to measure average latency.
    /// </summary>
    public async Task SendPingAsync()
    {
        List<ClientSession> pairedSessions;
        lock (_sessionSync)
        {
            pairedSessions = _sessions.Values.Where(session => session.IsPaired).ToList();
        }

        foreach (var session in pairedSessions)
        {
            var ping = ControlMessage.Create(MessageType.Ping, new PingPongPayload
            {
                SendTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });

            try
            {
                await SendControlMessageAsync(session, ping, CancellationToken.None);
            }
            catch
            {
            }
        }
    }

    private long CalculateAverageLatency()
    {
        lock (_sessionSync)
        {
            var samples = _sessions.Values
                .Where(session => session.IsPaired && session.LastLatencyMs > 0)
                .Select(session => session.LastLatencyMs)
                .ToList();

            return samples.Count == 0 ? 0 : (long)samples.Average();
        }
    }

    private void NotifyClientsChanged() => ClientsChanged?.Invoke();

    private void LogMessage(string msg) => Log?.Invoke($"[NetworkServer] {msg}");
}
