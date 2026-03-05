using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;

namespace SoundLink.Core.Discovery;

/// <summary>
/// Advertises the SoundLink server via mDNS/DNS-SD so Android clients can discover it.
/// Service type: _soundlink._tcp
/// </summary>
public sealed class MdnsAdvertiser : IDisposable
{
    public const string ServiceType = "_soundlink._tcp";
    public const string ServiceDomain = "local";

    private MulticastService? _mdns;
    private ServiceDiscovery? _sd;
    private ServiceProfile? _profile;
    private bool _isAdvertising;

    public bool IsAdvertising => _isAdvertising;

    public event Action<string>? Log;

    /// <summary>
    /// Starts advertising the SoundLink server on the network.
    /// </summary>
    public void Start(int controlPort, string? instanceName = null)
    {
        if (_isAdvertising) return;

        var name = instanceName ?? $"SoundLink on {Environment.MachineName}";

        _profile = new ServiceProfile(name, ServiceType, (ushort)controlPort);
        _profile.AddProperty("version", "1.0");
        _profile.AddProperty("platform", "windows");
        _profile.AddProperty("machine", Environment.MachineName);

        _mdns = new MulticastService();
        _sd = new ServiceDiscovery(_mdns);

        _sd.Advertise(_profile);

        _mdns.Start();
        _isAdvertising = true;
        LogMessage($"mDNS advertising started: {name} on port {controlPort}");
    }

    public void Stop()
    {
        if (!_isAdvertising) return;
        _isAdvertising = false;

        if (_profile != null && _sd != null)
        {
            try { _sd.Unadvertise(_profile); } catch { }
        }

        _mdns?.Stop();
        _sd?.Dispose();
        _mdns?.Dispose();

        _mdns = null;
        _sd = null;
        _profile = null;

        LogMessage("mDNS advertising stopped");
    }

    public void Dispose() => Stop();

    private void LogMessage(string msg) => Log?.Invoke($"[mDNS] {msg}");
}
