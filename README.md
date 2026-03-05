# SoundLink

SoundLink is a Windows desktop server plus an Android receiver app that relays the audio currently playing on a Windows PC to one or more Android phones.

The goal is simple: turn Android phones into low-latency speakers for the same Windows machine, either over the local network or over USB/ADB.

## What It Does Today

- Captures Windows output audio with WASAPI loopback
- Encodes the stream as 48 kHz stereo Opus
- Streams the same feed to multiple Android clients at once
- Discovers the Windows server on the local network with mDNS
- Pairs devices with a PIN over a TLS control channel
- Supports LAN audio over UDP and USB/ADB audio over TCP
- Lets the Android app control its own playback volume locally
- Runs as a small WPF desktop app with tray support

## What It Is Not

- It is not a general-purpose remote desktop tool
- It is not an iOS app
- It does not currently expose full media transport controls on the PC
- The volume slider in the Android app affects phone playback volume, not the Windows system volume

## Platforms

### Windows desktop server

- Windows 10 or newer
- .NET 8 SDK for development

### Android receiver

- Android 8.0 (API 26) or newer
- Android Studio or the Android SDK toolchain for builds

## Connection Modes

### LAN mode

- Control: TCP + TLS on port `7359`
- Audio: UDP on port `7360`
- Discovery: mDNS / DNS-SD

### USB mode

- Control: ADB reverse to local TCP `7359`
- Audio: ADB reverse to local TCP `7360`
- Android app entry point: `USB / ADB`

## Multi-Device Behavior

The current server can stream to multiple Android devices at the same time.

- Each device pairs independently with the same server PIN
- Each client keeps its own control session and audio transport
- The desktop app fans out the same encoded audio frames to all active clients
- Stopping one client does not stop the others

## Development Quick Start

### Windows app

```powershell
dotnet build src\SoundLink.Desktop\SoundLink.Desktop.csproj
dotnet run --project src\SoundLink.Desktop\SoundLink.Desktop.csproj
```

### Android app

```powershell
cd android
.\gradlew.bat assembleDebug
```

The debug APK is generated under:

`android/app/build/outputs/apk/debug/`

## Windows Distribution Options

### Lite build

The lightest Windows package is the framework-dependent single-file build.

- Smaller file size
- Requires the .NET 8 Desktop Runtime to already be installed on the target PC
- Recommended when download size matters more than zero-dependency distribution

Example:

```powershell
dotnet publish src\SoundLink.Desktop\SoundLink.Desktop.csproj /p:PublishProfile=WindowsLite
```

### Self-contained build

The self-contained Windows build bundles the .NET runtime inside the executable.

- Bigger file size
- No separate .NET runtime install required
- Better when distributing to unknown machines

## How To Use

### LAN

1. Start the Windows server.
2. Open the Android app.
3. Pick the discovered PC, or use manual connect.
4. Enter the PIN shown on the desktop app.
5. Audio from the PC will start playing on the phone.

### USB / ADB

1. Connect the phone to the PC with USB.
2. Enable USB debugging on the phone.
3. Enable `USB (ADB) connection` in the desktop app.
4. In the Android app, choose `USB / ADB`.
5. Enter the PIN shown on the desktop app.

## Project Layout

```text
SoundLink/
|- src/
|  |- SoundLink.Core/        # audio capture, encoding, networking, discovery, security
|  |- SoundLink.Desktop/     # WPF desktop server UI
|- android/
|  |- app/src/main/
|     |- java/com/soundlink/ # Kotlin Android client
|     |- cpp/                # native Opus decoder bridge
|- README.md
```

## License

MIT
