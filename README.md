OpenTv

IPTV player for Windows with built-in VPN support. Built with .NET 8, Avalonia UI 11, and LibVLCSharp.

Complete: M3U import, Xtream Codes, playback, WireGuard, TV guide (XMLTV)
Remaining: OpenVPN — see What's Remaining

Features
M3U / M3U8 playlist import
Local M3U file support
Xtream Codes support
Live TV playback
XMLTV / EPG TV guide
WireGuard VPN integration
Persistent VPN tunnels
Windows DPAPI password encryption
Standalone single-file .exe publishing
Self-contained deployment with bundled VLC
MVVM architecture
Platform-independent core logic designed for future Android/iOS support
Project Structure

The application logic is intentionally separated from the platform so that Core and Core.Vpn can be reused directly in a future Android/iOS version.

Project	Target	Contents
Core/	net8.0	M3U parsing, Xtream client, XMLTV parsing and EPG matching, data models, playlist loading, JSON storage. No Windows or UI dependencies.
Core.Vpn/	net8.0	Contracts only: IVpnService, VpnManager, VpnProfile, VpnState. Platform-independent.
Windows.Vpn/	net8.0-windows	WireGuard implementation using the official wireguard.exe and tunnel service. UAC handling.
Windows.UI/	net8.0-windows	Avalonia application using MVVM. Produces OpenTv.exe.

The dependency chain only goes in one direction:

Windows.UI
    ↓
Windows.Vpn
    ↓
Core.Vpn
    ↓
Core


Core has no knowledge of VPN, Windows, or Avalonia.

Requirements
.NET 8 SDK

Install the .NET 8 SDK and verify it with:

dotnet --version


The project has been verified with:

8.0.424

WireGuard for Windows

WireGuard is only required for VPN functionality.

Download it from the official website:

https://www.wireguard.com/install/

The application automatically detects whether WireGuard is installed and displays a message in the VPN tab if it is missing.

VLC

You do not need to install VLC manually.

The VLC engine is included through the NuGet package:

VideoLAN.LibVLC.Windows


It is packaged as part of the application build.

Building and Running

Build the complete solution:

dotnet build OpenTv.sln


Run the application:

dotnet run --project Windows.UI/OpenTv.Windows.UI.csproj

Publishing a Standalone .exe

Self-contained publishing includes the .NET runtime and VLC, so the recipient does not need anything else installed.

There are two deployment options.

Option 1: Single .exe

This is the recommended option for distributing the application.

dotnet publish Windows.UI/OpenTv.Windows.UI.csproj -c Release -r win-x64 --self-contained true -p:EmbedLibVlc=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish-single


This produces exactly one file:

publish-single\OpenTv.exe


Size: approximately 88 MB.

Nothing else is required.

First startup

On the first launch, the application extracts the VLC engine (~100 MB) to:

%LOCALAPPDATA%\OpenTv\runtime\libvlc-3.0.21\


This takes approximately one second.

Subsequent launches are immediate.

The runtime directory can be deleted at any time. It will automatically be recreated the next time the application starts.

Option 2: Folder Deployment

This option is faster to build and does not require an extraction step at startup.

dotnet publish Windows.UI/OpenTv.Windows.UI.csproj -c Release -r win-x64 --self-contained true -o publish


This produces:

publish\


The folder contains approximately 430 files totaling around 198 MB.

The entire folder must be distributed. OpenTv.exe alone will not work in this mode.

Why EmbedLibVlc Is Required

PublishSingleFile by itself is not sufficient for VLC.

.NET extracts embedded native libraries into a temporary directory. This breaks the directory structure required by VLC:

libvlc\
└── win-x64\
    └── plugins\


Without this structure, VLC cannot locate its codecs and Core.Initialize() reports:

Failed to load required native libraries


Setting:

EmbedLibVlc=true


solves this by embedding the VLC engine as a resource inside the assembly.

Windows.UI/VlcRuntime.cs extracts the engine during the first launch and provides LibVLCSharp with an explicit path.

Extraction takes place in a temporary neighboring directory and is only moved into place after the extraction has completed successfully. This prevents an interrupted first launch from leaving behind a partially extracted VLC installation.

The runtime directory also includes the VLC version in its name. When VLC is upgraded, a new runtime directory is therefore created instead of mixing plugins from different VLC versions.

Adding Channels

Go to:

Settings → Playlists → Add

Three source types are available:

Type	Fields
M3uUrl	URL to an M3U/M3U8 playlist
M3uFile	Path to a local .m3u file
Xtream	Server address, username, and password
Xtream Connection Test

Xtream sources provide a Test connection button.

It authenticates against the panel and displays:

Account status
Expiration date
Number of simultaneous connections

This makes it possible to detect incorrect credentials before attempting playback.

Test Playlist

The repository contains:

samples/test-playlist.m3u


It contains four public test streams:

Apple BipBop
Big Buck Bunny
Tears of Steel
Sintel

The test playlist is already configured in:

%APPDATA%\OpenTv\sources.json

TV Guide / EPG

An XMLTV EPG can be configured under a playlist's XMLTV EPG URL field.

The field is optional.

If it is not configured, OpenTv attempts to use the guide URL advertised by the source.

M3U

The application checks:

url-tvg
x-tvg-url


on the #EXTM3U line.

Xtream

For Xtream sources, the account's xmltv.php endpoint is generated automatically.

Supported Formats

The guide supports:

.xml
.xml.gz
Local XMLTV files
Remote XMLTV URLs

Gzip is detected using magic bytes rather than the file extension because IPTV providers frequently use incorrect extensions.

Guide Views

EPG information appears in two places.

Channel List

The channel list displays:

Currently airing program
Progress bar
TV Guide Window

The TV guide button opens a separate window containing:

Channels on the left
Schedule for the selected day on the right
Seven days of programming
Automatic scrolling to the currently airing program
Watch this channel action

The guide window is intentionally modeless. This allows the user to browse the schedule while continuing to watch the current channel.

Channel Matching

tvg-id is used when available and recognized by the guide.

If no matching ID is available, OpenTv falls back to channel-name matching.

Names are normalized to handle common IPTV naming conventions:

Country prefixes: SE:, |SE|, [US]
Quality suffixes: HD, FHD, 4K
Accents
Punctuation

For example:

SE: SVT 1 HD


and:

svt1


both normalize to:

svt1


Colon-separated names are only stripped when the prefix is a two-letter country code. A channel named:

MTV: Hits


therefore keeps its name.

The status bar reports the number of matched channels:

Guide: 118/240 channels

EPG Performance

XMLTV files between 50 and 200 MB are normal.

The parser is streaming-based and never builds a complete DOM.

Entries for channels that do not exist in the user's playlist are discarded while the file is being read. This provides the largest memory saving.

Only the following EPG window is retained:

12 hours into the past
8 days into the future
EPG Cache

Downloaded EPG data is cached in:

%LOCALAPPDATA%\OpenTv\epg\


The cache duration is six hours.

If an EPG update fails, the previous cached copy is used instead of leaving the application without a guide.

Generating Test EPG Data

The repository contains:

samples/generate-test-epg.ps1


Run it with:

pwsh samples/generate-test-epg.ps1


The generated guide uses the current date, so regenerate it when the existing test data becomes outdated.

For a denser schedule that is useful for testing scrolling:

pwsh samples/generate-test-epg.ps1 -SlotMinutes 5

Data Storage

Persistent application data is stored under:

%APPDATA%\OpenTv\

File	Contents
sources.json	Playlist profiles, last-used channel, volume
vpn-profiles.json	VPN profile metadata (name, type, path)
vpn\*.conf	Imported WireGuard configurations
crash.log	Only written when the application crashes during startup

Regenerable data is stored separately under:

%LOCALAPPDATA%\OpenTv\

Directory	Contents
epg\	Cached EPG downloads
runtime\	Extracted VLC engine used by the single-file build

These directories can be safely deleted. They will be recreated when required.

Atomic Configuration Writes

Configuration files are written to a temporary file and then swapped into place atomically.

This prevents a crash during saving from leaving behind a partially written configuration.

If a configuration is nevertheless corrupted, OpenTv renames it to:

*.corrupt


and starts with empty settings instead of refusing to launch.

Xtream Password Security

Xtream passwords are encrypted using Windows DPAPI and bound to the current Windows user account before being written to sources.json.

If the configuration file is copied to another machine or Windows user account, the password cannot be decrypted.

OpenTv then asks for the password again rather than sending encrypted data to the provider.

Plain-text passwords from older sources.json files remain supported. They are automatically encrypted the next time the profile is saved.

Important: The Xtream protocol sends usernames and passwords as query parameters in the URL. This is how Xtream panels work and is not specific to OpenTv. Request URLs are therefore removed from error messages so passwords cannot accidentally appear in logs.

VPN Integration

OpenTv controls the official WireGuard binaries rather than embedding WireGuardNT through P/Invoke.

This provides signed drivers without requiring OpenTv to maintain its own driver.

Connecting

The application runs:

wireguard.exe /installtunnelservice <config>


This registers and starts:

WireGuardTunnel$<name>


as a Windows service.

Disconnecting

The application runs:

wireguard.exe /uninstalltunnelservice <name>


OpenTv does not rely on the process exit code to determine whether the tunnel is active.

Instead, it polls the actual Windows service state before reporting that the tunnel is connected.

Tunnel Naming

The tunnel name is derived from the configuration filename by wireguard.exe.

For this reason, names are sanitized during import.

For example:

My VPN (Sweden)!


becomes:

My-VPN-Sweden


Names are also made unique because the tunnel name becomes part of the Windows service name.

UAC

OpenTv runs as a normal user using:

asInvoker


in app.manifest.

The entire media player does not run as administrator.

Only tunnel operations require elevation. They use ShellExecute with the runas verb.

As a result, users receive:

One UAC prompt per connect/disconnect operation

rather than a UAC prompt every time the application starts.

If the user cancels the UAC prompt, OpenTv reports a clear error rather than incorrectly treating the operation as successful.

VPN Tunnel Persistence

Closing OpenTv does not disconnect the VPN tunnel.

This is intentional. Disconnecting automatically when the application closes could unexpectedly expose the user's normal network connection.

When OpenTv starts, it detects and adopts an already-running tunnel through:

RefreshAsync


A watchdog checks the tunnel every five seconds and detects if it has been stopped externally.

Known Limitations
Channel Logos

Channel logos are not currently displayed.

Xtream provides:

stream_icon


and M3U provides:

tvg-logo


Both values are stored on Channel, but displaying remote logos requires an asynchronous image loader.

Video Controls

Controls are displayed below the video rather than overlaid on top of it.

VideoView is a native child window, which prevents Avalonia from compositing controls over it.

A TiviMate-style overlay would require either:

VideoView.Content
Rendering through WriteableBitmap
WireGuard Connection Status

"Connected" currently means that the WireGuard service is running.

This confirms that the adapter is active but does not prove that the handshake with the peer succeeded.

Verifying the handshake requires:

wg show

Xtream

Only live TV is currently supported.

VOD and series require additional Xtream actions:

get_vod_streams
get_series


These are not implemented yet.

TV Guide Layout

The current TV guide displays one channel at a time.

It does not yet provide a TiviMate-style timeline grid showing all channels against a shared time axis.

Implementing this requires a custom virtualized layout panel.

Sidebar

The sidebar currently has a fixed width of:

330 px


There is no splitter.

What's Remaining
1. OpenVPN

The IVpnService contract and VpnManager routing are already in place.

The remaining implementation should control an openvpn.exe process through its management interface using a TCP socket.

This will provide:

Real-time connection status
Connection events
Logs
2. Timeline Grid

Implement a TiviMate-style EPG timeline:

Channel 1  | Program       | Program       | Program
Channel 2  |     Program   | Program       | Program
Channel 3  | Program       |      Program  | Program
           +---------------+---------------+-------->
                         Time


All required EPG data already exists in EpgGuide.

Only the layout panel is missing.

3. Unit Tests

The following components have been verified using one-off test harnesses:

M3uParser
XtreamClient
XmltvParser

They should be converted into proper repository tests because these components are intended to be reused on mobile platforms.

Verification
Area	Result
M3U parsing	Commas in channel names, #EXTGRP, #EXTVLCOPT, duplicates, malformed lines
Xtream client	22 checks against a mock panel with inconsistent JSON types
XMLTV / EPG	35 checks covering timestamp formats, name normalization, channel filtering, now/next, gzip, and XXE protection
WireGuard	Full tunnel lifecycle: import → connect → service + network adapter created → adopt after restart → disconnect → everything removed
DPAPI	Round-trip verified; incorrect entropy rejected
Playback	HLS test stream, embedded in window, D3D11VA hardware decoding
Publishing	Both modes verified: single-file .exe (88 MB; cold start extracts the engine in ~1 second, warm start is immediate) and folder deployment
License

Add the project's license information here.

If the project is not yet licensed, consider adding a LICENSE file before distributing OpenTv publicly.
