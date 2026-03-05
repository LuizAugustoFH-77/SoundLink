using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SoundLink.Core.Usb;

/// <summary>
/// Manages ADB reverse port forwarding for USB connections.
/// When active, Android connects to localhost on the device and ADB forwards the TCP ports to the PC.
/// </summary>
public sealed class AdbService : IDisposable
{
    private bool _isForwarding;

    public bool IsForwarding => _isForwarding;
    public bool IsAdbAvailable { get; private set; }

    public event Action<string>? Log;

    /// <summary>
    /// Checks if ADB is available in PATH or common install locations.
    /// </summary>
    public bool CheckAdbAvailable()
    {
        IsAdbAvailable = RunAdbCommand("version") != null;
        return IsAdbAvailable;
    }

    /// <summary>
    /// Sets up reverse port forwarding so the Android device can connect via USB.
    /// </summary>
    public bool StartForwarding(int controlPort, int audioPort)
    {
        if (_isForwarding) return true;
        if (!CheckAdbAvailable()) return false;

        var r1 = RunAdbCommand($"reverse tcp:{controlPort} tcp:{controlPort}");
        var r2 = RunAdbCommand($"reverse tcp:{audioPort} tcp:{audioPort}");

        if (r1 != null && r2 != null)
        {
            _isForwarding = true;
            LogMessage($"ADB reverse forwarding active: ports {controlPort}, {audioPort}");
            return true;
        }

        LogMessage("Failed to set up ADB reverse forwarding");
        return false;
    }

    public void StopForwarding()
    {
        if (!_isForwarding) return;
        RunAdbCommand("reverse --remove-all");
        _isForwarding = false;
        LogMessage("ADB reverse forwarding removed");
    }

    public void Dispose() => StopForwarding();

    private string? RunAdbCommand(string arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "adb",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private void LogMessage(string msg) => Log?.Invoke($"[ADB] {msg}");
}
