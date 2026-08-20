using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace WireTv.Windows.Vpn;

/// <summary>
/// Finds the official WireGuard for Windows installation and validates tunnel names.
/// WireTv drives wireguard.exe rather than embedding WireGuardNT, so the user
/// installs WireGuard once and WireTv reuses its signed driver and tunnel service.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WireGuardLocator
{
    /// <summary>
    /// WireGuard rejects tunnel names outside this set, and the name also becomes
    /// part of a Windows service name, so it is validated before use.
    /// </summary>
    private static readonly Regex ValidTunnelName = new("^[a-zA-Z0-9_=+.-]{1,32}$", RegexOptions.Compiled);

    public const string DownloadUrl = "https://www.wireguard.com/install/";

    /// <summary>Full path to wireguard.exe, or null when WireGuard is not installed.</summary>
    public static string? FindExecutable()
    {
        foreach (var candidate in CandidatePaths())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> CandidatePaths()
    {
        // Standard install locations first.
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wireguard.exe");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WireGuard", "wireguard.exe");

        // Then anything on PATH, which covers portable or non-default installs.
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            yield break;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string combined;

            try
            {
                combined = Path.Combine(directory.Trim(), "wireguard.exe");
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry must not break detection.
                continue;
            }

            yield return combined;
        }
    }

    /// <summary>The Windows service WireGuard creates for a tunnel of the given name.</summary>
    public static string ServiceNameFor(string tunnelName) => "WireGuardTunnel$" + tunnelName;

    public static bool IsValidTunnelName(string? name)
        => !string.IsNullOrEmpty(name) && ValidTunnelName.IsMatch(name);

    /// <summary>
    /// Turns an arbitrary user-supplied profile name into something WireGuard accepts.
    /// Invalid characters collapse into single dashes; the result is capped at 32 chars.
    /// </summary>
    public static string SanitizeTunnelName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "opentv";

        var builder = new System.Text.StringBuilder(name.Length);
        var lastWasDash = false;

        foreach (var c in name.Trim())
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '=' or '+' or '.' or '-')
            {
                builder.Append(c);
                lastWasDash = c == '-';
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        var result = builder.ToString().Trim('-');

        if (result.Length == 0)
            return "opentv";

        return result.Length > 32 ? result[..32].TrimEnd('-') : result;
    }
}
