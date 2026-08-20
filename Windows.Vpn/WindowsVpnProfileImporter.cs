using System.Runtime.Versioning;
using WireTv.Core.Vpn;

namespace WireTv.Windows.Vpn;

/// <summary>
/// Adapts the static <see cref="WireGuardProfileImporter"/> to the platform-neutral
/// contract the shared UI depends on.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVpnProfileImporter : IVpnProfileImporter
{
    public VpnProfile Import(
        string sourceConfigPath,
        IReadOnlyCollection<VpnProfile> existingProfiles,
        string? displayName = null)
        => WireGuardProfileImporter.Import(sourceConfigPath, existingProfiles, displayName);

    public void DeleteConfig(VpnProfile profile) => WireGuardProfileImporter.DeleteConfig(profile);
}
