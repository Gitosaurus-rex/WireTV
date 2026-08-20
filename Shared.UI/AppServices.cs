using WireTv.Core.Epg;
using WireTv.Core.Playlists;
using WireTv.Core.Storage;
using WireTv.Core.Vpn;

namespace WireTv.UI;

/// <summary>
/// Application-wide singletons shared by every platform head.
///
/// A hand-rolled composition root rather than a DI container: the object graph is
/// small and fixed. The platform-specific pieces - how secrets are encrypted, which
/// VPN backends exist - are injected by the head through <see cref="Initialize"/>,
/// so this file stays free of any Windows or Android reference.
/// </summary>
public static class AppServices
{
    private static ISecretProtector _protector = PlaintextSecretProtector.Instance;
    private static IVpnService[] _vpnBackends = [];

    /// <summary>How VPN configurations are brought in. Null when the platform has no importer.</summary>
    public static IVpnProfileImporter? VpnProfileImporter { get; private set; }

    private static readonly Lazy<PlaylistSourceStore> LazySourceStore =
        new(() => new PlaylistSourceStore(protector: _protector));

    private static readonly Lazy<VpnProfileStore> LazyVpnProfileStore = new(() => new VpnProfileStore());
    private static readonly Lazy<PlaylistLoader> LazyPlaylistLoader = new(() => new PlaylistLoader());
    private static readonly Lazy<EpgLoader> LazyEpgLoader = new(() => new EpgLoader());
    private static readonly Lazy<VpnManager> LazyVpn = new(() => new VpnManager(_vpnBackends));

    /// <summary>
    /// Supplies the platform pieces. Must run before any service is first touched,
    /// because the Lazy instances capture these on first access.
    /// </summary>
    /// <param name="protector">
    /// How stored credentials are encrypted. Windows passes a DPAPI implementation;
    /// a head with nothing better passes the plaintext one explicitly.
    /// </param>
    /// <param name="vpnBackends">
    /// VPN implementations available on this platform. Empty is valid and simply
    /// means the VPN controls stay disabled.
    /// </param>
    /// <param name="vpnProfileImporter">
    /// How VPN configurations are imported. Null disables the import button rather
    /// than failing at the point of use.
    /// </param>
    public static void Initialize(
        ISecretProtector? protector = null,
        IEnumerable<IVpnService>? vpnBackends = null,
        IVpnProfileImporter? vpnProfileImporter = null)
    {
        if (LazySourceStore.IsValueCreated || LazyVpn.IsValueCreated)
            throw new InvalidOperationException("AppServices was already used; initialise it before first access.");

        _protector = protector ?? PlaintextSecretProtector.Instance;
        _vpnBackends = vpnBackends?.ToArray() ?? [];
        VpnProfileImporter = vpnProfileImporter;
    }

    public static PlaylistSourceStore SourceStore => LazySourceStore.Value;

    public static VpnProfileStore VpnProfiles => LazyVpnProfileStore.Value;

    public static PlaylistLoader PlaylistLoader => LazyPlaylistLoader.Value;

    public static EpgLoader EpgLoader => LazyEpgLoader.Value;

    public static VpnManager Vpn => LazyVpn.Value;

    /// <summary>True when this platform has any VPN backend at all.</summary>
    public static bool HasVpnSupport => _vpnBackends.Length > 0;

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

/// <summary>Startup problems worth surfacing in the UI instead of crashing silently.</summary>
public static class StartupDiagnostics
{
    public static Exception? LibVlcFailure { get; set; }

    public static void WriteCrashLog(Exception ex)
    {
        try
        {
            File.AppendAllText(
                AppPaths.InData("crash.log"),
                $"[{DateTimeOffset.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // A failure to log must never mask the original exception.
        }
    }
}
