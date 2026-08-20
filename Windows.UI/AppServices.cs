using OpenTv.Core.Epg;
using OpenTv.Core.Playlists;
using OpenTv.Core.Storage;
using OpenTv.Core.Vpn;
using OpenTv.Windows.UI.Services;
using OpenTv.Windows.Vpn;

namespace OpenTv.Windows.UI;

/// <summary>
/// Application-wide singletons.
///
/// A hand-rolled composition root rather than a DI container: the object graph is
/// small and fixed, and this keeps the dependency list short. The important part is
/// that the Windows-specific backends are wired up here and nowhere else, so
/// swapping in Android/iOS implementations later touches only this file.
/// </summary>
internal static class AppServices
{
    private static readonly Lazy<PlaylistSourceStore> LazySourceStore = new(
        () => new PlaylistSourceStore(protector: new DpapiSecretProtector()));
    private static readonly Lazy<VpnProfileStore> LazyVpnProfileStore = new(() => new VpnProfileStore());
    private static readonly Lazy<PlaylistLoader> LazyPlaylistLoader = new(() => new PlaylistLoader());
    private static readonly Lazy<EpgLoader> LazyEpgLoader = new(() => new EpgLoader());

    private static readonly Lazy<VpnManager> LazyVpn = new(() => new VpnManager(
    [
        // OpenVPN joins this list in iteration 2; VpnManager already routes by kind.
        new WireGuardVpnService()
    ]));

    public static PlaylistSourceStore SourceStore => LazySourceStore.Value;

    public static VpnProfileStore VpnProfiles => LazyVpnProfileStore.Value;

    public static PlaylistLoader PlaylistLoader => LazyPlaylistLoader.Value;

    public static EpgLoader EpgLoader => LazyEpgLoader.Value;

    public static VpnManager Vpn => LazyVpn.Value;

    public static void Shutdown()
    {
        // Only dispose what was actually created, so shutdown after an early failure
        // does not construct services just to tear them down.
        if (LazyPlaylistLoader.IsValueCreated)
            LazyPlaylistLoader.Value.Dispose();

        if (LazyEpgLoader.IsValueCreated)
            LazyEpgLoader.Value.Dispose();

        if (LazyVpn.IsValueCreated)
            LazyVpn.Value.Dispose();
    }
}
