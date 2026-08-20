# OpenTv

**Windows IPTV player with built-in VPN support.**

Built with **.NET 8**, **Avalonia UI 11**, and **LibVLCSharp**.

## Features

* M3U / M3U8 playlists
* Xtream Codes
* Live TV playback
* XMLTV / EPG guide
* WireGuard VPN
* Persistent VPN tunnels
* DPAPI-encrypted passwords
* Self-contained single `.exe`
* Bundled VLC runtime
* MVVM architecture
* Platform-independent core for future Android/iOS support

## Stack

```text
Windows.UI
    ↓
Windows.Vpn
    ↓
Core.Vpn
    ↓
Core
```

`Core` contains all IPTV/EPG logic and has no Windows or UI dependencies.

## Requirements

* **.NET 8 SDK**
* **WireGuard for Windows** for VPN features
* VLC does **not** need to be installed manually

## Build

```bash
dotnet build OpenTv.sln
```

Run:

```bash
dotnet run --project Windows.UI/OpenTv.Windows.UI.csproj
```

## Publish

### Single `.exe`

Recommended for distribution:

```bash
dotnet publish Windows.UI/OpenTv.Windows.UI.csproj -c Release -r win-x64 --self-contained true -p:EmbedLibVlc=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-single
```

Produces:

```text
publish-single/OpenTv.exe
```

~88 MB. No dependencies required.

VLC is extracted automatically on first launch.

### Folder

```bash
dotnet publish Windows.UI/OpenTv.Windows.UI.csproj -c Release -r win-x64 --self-contained true -o publish
```

Distribute the entire folder.

## IPTV

Add playlists via:

**Settings → Playlists → Add**

Supports:

* M3U URL
* Local M3U files
* Xtream Codes

A test playlist is included at:

```text
samples/test-playlist.m3u
```

## EPG

Supports XMLTV from:

* URLs
* `.xml`
* `.xml.gz`
* Local files
* Xtream `xmltv.php`

Includes channel matching, now/next information, 7-day schedules, caching, and large XMLTV file support.

## VPN

WireGuard is controlled through the official Windows binaries.

* Connect/disconnect with UAC only when needed
* Existing tunnels survive app restarts
* Tunnel state is monitored automatically

## Storage

```text
%APPDATA%\OpenTv\
%LOCALAPPDATA%\OpenTv\
```

Stores playlists, VPN profiles, EPG cache, and the VLC runtime.

Passwords are encrypted using **Windows DPAPI**.

## Known Limitations

* No OpenVPN yet
* No channel logos
* No VOD / series
* No EPG timeline grid
* Video controls are below the video
* WireGuard "Connected" does not verify peer handshake
* Sidebar is fixed at 330px

## What's Next

1. **OpenVPN**
2. **TiviMate-style EPG timeline**
3. **Proper unit tests**

## License

A license still needs to be added before public distribution.
