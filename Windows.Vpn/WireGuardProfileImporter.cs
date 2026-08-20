using System.Runtime.Versioning;
using WireTv.Core.Vpn;

namespace WireTv.Windows.Vpn;

/// <summary>
/// Copies a user-supplied .conf into the directory WireTv owns.
///
/// wireguard.exe derives the tunnel name from the config file's base name, and that
/// name also becomes part of a Windows service name, so the file is renamed to a
/// sanitised, unique name at import time. Keeping our own copy also means the
/// profile keeps working if the user moves or deletes the original download.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WireGuardProfileImporter
{
    /// <summary>Sanity limit: WireGuard configs are a few kB, anything larger is not one.</summary>
    private const long MaxConfigBytes = 256 * 1024;

    public static VpnProfile Import(
        string sourceConfigPath,
        IReadOnlyCollection<VpnProfile> existingProfiles,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(existingProfiles);

        if (!File.Exists(sourceConfigPath))
            throw new VpnException($"The file does not exist: {sourceConfigPath}");

        var info = new FileInfo(sourceConfigPath);

        if (info.Length > MaxConfigBytes)
            throw new VpnException("That file is too large to be a WireGuard configuration.");

        var content = File.ReadAllText(sourceConfigPath);
        Validate(content);

        var name = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileNameWithoutExtension(sourceConfigPath)
            : displayName.Trim();

        var tunnelName = MakeUniqueTunnelName(WireGuardLocator.SanitizeTunnelName(name), existingProfiles);

        var targetPath = Path.Combine(VpnProfileStore.ConfigDirectory, tunnelName + ".conf");
        File.Copy(sourceConfigPath, targetPath, overwrite: true);

        return new VpnProfile
        {
            Name = name,
            Kind = VpnKind.WireGuard,
            ConfigPath = targetPath,
            ImportedUtc = DateTimeOffset.UtcNow
        };
    }

    /// <summary>Deletes the config file backing a profile. Safe to call for a profile already gone.</summary>
    public static void DeleteConfig(VpnProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            if (!string.IsNullOrWhiteSpace(profile.ConfigPath) && File.Exists(profile.ConfigPath))
                File.Delete(profile.ConfigPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Removing the profile from the list matters more than cleaning up the file.
        }
    }

    /// <summary>
    /// Cheap structural check so an obviously wrong file is rejected at import time
    /// rather than as an opaque failure during the first connection attempt.
    /// </summary>
    private static void Validate(string content)
    {
        if (!content.Contains("[Interface]", StringComparison.OrdinalIgnoreCase))
            throw new VpnException("That file has no [Interface] section, so it is not a WireGuard configuration.");

        if (!content.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase))
            throw new VpnException("The configuration has no PrivateKey, so the tunnel could not authenticate.");

        if (!content.Contains("[Peer]", StringComparison.OrdinalIgnoreCase))
            throw new VpnException("The configuration has no [Peer] section, so there is nothing to connect to.");
    }

    private static string MakeUniqueTunnelName(string baseName, IReadOnlyCollection<VpnProfile> existing)
    {
        var taken = existing
            .Where(p => p.Kind == VpnKind.WireGuard && !string.IsNullOrWhiteSpace(p.ConfigPath))
            .Select(p => Path.GetFileNameWithoutExtension(p.ConfigPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!taken.Contains(baseName) && !File.Exists(Path.Combine(VpnProfileStore.ConfigDirectory, baseName + ".conf")))
            return baseName;

        for (var suffix = 2; suffix < 1000; suffix++)
        {
            // Keep room for the suffix inside WireGuard's 32-character limit.
            var trimmed = baseName.Length > 27 ? baseName[..27].TrimEnd('-') : baseName;
            var candidate = $"{trimmed}-{suffix}";

            if (!taken.Contains(candidate) &&
                !File.Exists(Path.Combine(VpnProfileStore.ConfigDirectory, candidate + ".conf")))
            {
                return candidate;
            }
        }

        throw new VpnException($"Could not derive a free tunnel name from '{baseName}'.");
    }
}
