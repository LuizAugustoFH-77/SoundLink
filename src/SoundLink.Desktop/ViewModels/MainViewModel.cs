using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundLink.Core;
using SoundLink.Core.Audio;

namespace SoundLink.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly SoundLinkServer _server;
    private readonly DispatcherTimer _statusTimer;

    [ObservableProperty] private string _statusText = "Stopped";
    [ObservableProperty] private string _pinDisplay = "------";
    [ObservableProperty] private string _connectedDevice = "";
    [ObservableProperty] private long _latencyMs;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isStreaming;
    [ObservableProperty] private bool _usbEnabled;
    [ObservableProperty] private string _selectedDeviceId = "";
    [ObservableProperty] private int _bitrate = 128;
    [ObservableProperty] private ObservableCollection<AudioDeviceInfo> _audioDevices = new();
    [ObservableProperty] private ObservableCollection<string> _logEntries = new();

    public MainViewModel()
    {
        _server = new SoundLinkServer();

        _server.Log += msg => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            LogEntries.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
            while (LogEntries.Count > 200) LogEntries.RemoveAt(LogEntries.Count - 1);
        });

        _server.Network.ClientConnected += name => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            RefreshConnectionState();
        });

        _server.Network.ClientDisconnected += () => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            RefreshConnectionState();
        });

        _server.Network.ClientsChanged += () => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            RefreshConnectionState();
        });

        _server.Network.LatencyMeasured += ms => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            LatencyMs = ms;
        });

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatus();

        LoadAudioDevices();
    }

    private void LoadAudioDevices()
    {
        AudioDevices.Clear();
        foreach (var dev in AudioCaptureService.GetOutputDevices())
            AudioDevices.Add(dev);
    }

    [RelayCommand]
    private async Task StartServerAsync()
    {
        if (IsRunning) return;

        try
        {
            var deviceId = string.IsNullOrEmpty(SelectedDeviceId) ? null : SelectedDeviceId;
            _server.Encoder.Bitrate = Bitrate * 1000;
            await _server.StartAsync(deviceId, UsbEnabled);

            IsRunning = true;
            PinDisplay = _server.CurrentPin;
            StatusText = "Waiting for connection...";
            _statusTimer.Start();
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void StopServer()
    {
        _server.Stop();
        IsRunning = false;
        IsConnected = false;
        IsStreaming = false;
        PinDisplay = "------";
        StatusText = "Stopped";
        _statusTimer.Stop();
    }

    [RelayCommand]
    private void RegeneratePin()
    {
        if (!IsRunning) return;
        PinDisplay = _server.Network.RegeneratePin();
    }

    [RelayCommand]
    private void RefreshDevices() => LoadAudioDevices();

    private void UpdateStatus()
    {
        if (!IsRunning) return;
        IsStreaming = _server.Network.IsStreaming;
        IsConnected = _server.Network.IsPaired;
        RefreshConnectionState();
    }

    private void RefreshConnectionState()
    {
        var connectedCount = _server.Network.ConnectedClientCount;
        IsConnected = connectedCount > 0;
        IsStreaming = _server.Network.IsStreaming;
        ConnectedDevice = _server.Network.ConnectedClientSummary;

        if (!IsRunning)
        {
            StatusText = "Stopped";
            return;
        }

        StatusText = connectedCount switch
        {
            0 => "Waiting for connection...",
            1 => $"Connected: {ConnectedDevice}",
            _ => $"Connected: {connectedCount} devices"
        };

        if (connectedCount == 0)
        {
            PinDisplay = _server.CurrentPin;
        }
    }

    public void Dispose()
    {
        _statusTimer.Stop();
        _server.Dispose();
    }
}
