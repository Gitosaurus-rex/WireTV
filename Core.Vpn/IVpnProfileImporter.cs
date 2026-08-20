namespace WireTv.Core.Vpn;

/// <summary>
/// Brings a user-supplied VPN configuration under the app's control.
///
/// Platform-specific because the rules differ: on Windows a WireGuard tunnel name
/// is derived from the config filename and becomes part of a service name, while
/// an Android head would hand the file to the system VPN service instead.
/// </summary>
public interface IVpnProfileImporter
{
    /// <summary>
    /// Copies <paramref name="sourceConfigPath"/> into app-owned storage and returns
    /// the resulting profile. Throws <see cref="VpnException"/> when the file is not
    /// a usable configuration, so the failure surfaces at import rather than at
    /// connect time.
    /// </summary>
    VpnProfile Import(
        string sourceConfigPath,
        IReadOnlyCollection<VpnProfile> existingProfiles,
        string? displayName = null);

    /// <summary>Removes the stored configuration. Safe to call for a profile already gone.</summary>
    void DeleteConfig(VpnProfile profile);
}
