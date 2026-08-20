using WireTv.Core.Storage;

namespace WireTv.Core.Vpn;

/// <summary>Root document persisted to vpn-profiles.json.</summary>
public sealed class VpnProfileDocument
{
    public List<VpnProfile> Profiles { get; set; } = new();

    /// <summary>Id of the profile the user last connected with.</summary>
    public string? LastUsedProfileId { get; set; }
}

/// <summary>
/// Persists imported VPN profiles. Only metadata and a path live here - the
/// config files themselves (which contain private keys) stay as files on disk
/// under <see cref="ConfigDirectory"/>.
/// </summary>
public sealed class VpnProfileStore
{
    public const string FileName = "vpn-profiles.json";

    private readonly JsonStore<VpnProfileDocument> _store;

    public VpnProfileStore(string? filePath = null)
    {
        _store = new JsonStore<VpnProfileDocument>(filePath ?? AppPaths.InData(FileName));
    }

    /// <summary>Where imported .conf/.ovpn files are copied to.</summary>
    public static string ConfigDirectory => AppPaths.EnsureSubDirectory("vpn");

    public string FilePath => _store.FilePath;

    public Task<VpnProfileDocument> LoadAsync(CancellationToken ct = default) => _store.LoadAsync(ct);

    public Task SaveAsync(VpnProfileDocument document, CancellationToken ct = default)
        => _store.SaveAsync(document, ct);
}
