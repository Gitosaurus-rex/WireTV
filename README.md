# WireTV

**IPTV player for the TV, with built-in VPN support.**

Built with **.NET 8/9**, **Avalonia UI 11**, and **LibVLCSharp**.
Runs on **Windows** and **Android TV** from one shared interface.

## Features

* M3U / M3U8 playlists
* Xtream Codes
* Live TV playback
* XMLTV / EPG guide
* Ten-foot TV interface, D-pad only
* Overlays composited on top of full-screen video
* WireGuard VPN (Windows)
* Persistent VPN tunnels
* DPAPI-encrypted passwords
* Self-contained single `.exe` and a sideloadable `.apk`
* Bundled VLC runtime
* MVVM architecture

## Stack

```text
Windows.UI          Droid.UI
        \            /
         Shared.UI          Windows.Vpn
              \                 /
               Core.Vpn ‹------­+
                   ↓
                 Core
```

`Core` holds all IPTV/EPG logic. `Shared.UI` holds the entire interface. Neither
knows anything about Windows or Android — the platform pieces (credential
encryption, VPN backends, profile import, dialogs) are injected by each head
through `AppServices.Initialize`.

## TV interface

Modelled on TiviMate: video plays full screen at all times, everything else is a
layer on top. Designed for a remote with **only a D-pad, OK and Back** — no
dedicated Guide, Menu or colour buttons.

The channel drawer and the guide are columns you walk with Left and Right:

```text
drawer:  [ TV guide ]  [ Groups ]  [ Channels ]
         [ Settings ]

guide:   [ Today    ]  [ Channels ]  [ Schedule ]
         [ Tomorrow ]
```

| Context | Key | Action |
|---|---|---|
| Video | OK | Open the channel drawer |
| Video | Up / Down | Change channel |
| Video | Back | Exit |
| Drawer / guide | Left / Right | Move between columns |
| Drawer / guide | Up / Down | Move within a column |
| Drawer / guide | OK | Watch, or open the menu entry |
| Anywhere | Back | One layer back |

Arrowing through the drawer previews only; OK commits. That is the difference
between browsing the list and zapping through fifty channels on the way to the
one you wanted.

## Video rendering

LibVLCSharp's `VideoView` is a `NativeControlHost` — on Windows a child HWND,
which always paints over its parent. No Avalonia control can be composited on top
of it, which rules out the overlays a TV interface is made of. `VideoView.Content`
does not composite either.

`Shared.UI/Controls/VideoSurface.cs` instead has libvlc decode into buffers the
app owns (its `vmem` output) and draws the frames as an ordinary image. Overlays,
transparency and animation then behave like any other content, and the same path
works on Android.

Cost is roughly **7% of one core at 1080p**. Frames larger than 1080p are scaled
down by libvlc first, since the per-frame cost scales with area.

## Requirements

* **.NET 8 SDK** for Windows
* **.NET 9 SDK**, JDK 17 and the Android SDK for the APK
* **WireGuard for Windows** for VPN features
* VLC does **not** need to be installed manually

## Build

```bash
dotnet build WireTv.sln
```

Run:

```bash
dotnet run --project Windows.UI/WireTv.Windows.UI.csproj
```

## Publish

### Single `.exe`

```bash
dotnet publish Windows.UI/WireTv.Windows.UI.csproj -c Release -r win-x64 --self-contained true -p:EmbedLibVlc=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish-single
```

Produces `publish-single/WireTV.exe`, ~88 MB, no dependencies.
VLC is extracted to `%LOCALAPPDATA%\WireTV\runtime\` on first launch (~1 second).

`EmbedLibVlc` is required: `PublishSingleFile` alone flattens native libraries
into a temp directory, which destroys the `libvlc/win-x64/plugins` layout VLC
needs to find its codecs.

### Folder

```bash
dotnet publish Windows.UI/WireTv.Windows.UI.csproj -c Release -r win-x64 --self-contained true -o publish
```

Distribute the entire folder.

### Android APK

Provision the Android SDK once:

```bash
dotnet build Droid.UI/WireTv.Droid.UI.csproj -t:InstallAndroidDependencies -p:AndroidSdkDirectory="%LOCALAPPDATA%\Android\Sdk" -p:AcceptAndroidSDKLicenses=True
```

Then:

```bash
dotnet publish Droid.UI/WireTv.Droid.UI.csproj -c Release
```

Produces `com.wiretv.player-Signed.apk`, ~96 MB, containing `arm64-v8a` and
`armeabi-v7a`. Signed with the Android debug key — enough for sideloading, not
for a store.

The manifest declares `LEANBACK_LAUNCHER` alongside the normal launcher category,
so one APK installs on both a TV and a phone.

Three settings exist because the app would not otherwise start:

* Trimming and profiled AOT are **off** — both strip code only reachable from XAML
* The activity theme descends from `Theme.AppCompat` — `AvaloniaMainActivity` is an
  `AppCompatActivity` and throws in `onCreate` otherwise
* Remote input is read in `DispatchKeyEvent` — Avalonia routes key events from the
  focused element, and a freshly launched activity focuses nothing

## IPTV

**Settings → Add**. Supports M3U URLs, local M3U files, and Xtream Codes.
Xtream sources have a *Test connection* button that reports account status,
expiry and connection count before you try to play anything.

A test playlist is included at `samples/test-playlist.m3u`, and
`samples/generate-test-epg.ps1` generates matching guide data.

## EPG

XMLTV from a URL or a local file, `.xml` or `.xml.gz` (detected by magic bytes,
not the extension — providers get it wrong often enough to matter). Xtream sources
build their `xmltv.php` URL automatically.

Channels match on `tvg-id` first, then on a normalised name that strips country
prefixes (`SE:`, `|SE|`, `[US]`), quality suffixes (`HD`, `FHD`, `4K`), accents and
punctuation. The colon form is only stripped for a two-letter code, so `MTV: Hits`
keeps its name.

Parsing is streaming and discards channels absent from your playlist while
reading, which is what makes 50–200 MB guides workable. Downloads are cached for
six hours in `%LOCALAPPDATA%\WireTV\epg\`.

## VPN

WireGuard is driven through the official Windows binaries rather than an embedded
driver.

* One UAC prompt per connect/disconnect, not one per app launch
* Tunnels survive app restarts and are re-adopted on startup
* Tunnel state is polled, so "connected" reflects the service, not an exit code

Not in the Android build: Android tunnels go through the system `VpnService` API,
a separate implementation. The VPN controls stay hidden there rather than
half-working.

## Storage

```text
%APPDATA%\WireTV\        playlists, VPN profiles
%LOCALAPPDATA%\WireTV\   EPG cache, extracted VLC runtime
```

Passwords are encrypted with **Windows DPAPI**, bound to the current user.

Renaming from OpenTv migrates automatically: the old data directory is moved on
first launch, and stored passwords still decrypt via the previous entropy before
being re-encrypted on the next save.

## Known limitations

* No OpenVPN yet
* No channel logos
* No VOD / series
* No EPG timeline grid
* WireGuard "Connected" does not verify the peer handshake
* Android playback is unverified on real hardware
* Android has no VPN and no message dialogs

## What's next

1. **OpenVPN** via the management interface
2. **TiviMate-style EPG timeline grid**
3. **Proper unit tests** for the parsers

## License

A license still needs to be added before public distribution.
