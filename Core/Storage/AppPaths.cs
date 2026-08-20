namespace WireTv.Core.Storage;

/// <summary>
/// Resolves where WireTV keeps its user data. Uses SpecialFolder rather than
/// hard-coded Windows paths so an Android/iOS head resolves correctly too.
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "WireTV";

    /// <summary>
    /// The name used before the app was renamed. An existing installation keeps its
    /// playlists, VPN profiles and saved credentials because the old directory is
    /// moved across on first run rather than abandoned.
    /// </summary>
    private const string LegacyAppFolderName = "OpenTv";

    /// <summary>Per-user data directory. Created on first access.</summary>
    public static string DataDirectory { get; } = CreateUnder(Environment.SpecialFolder.ApplicationData);

    /// <summary>
    /// Machine-local directory for regenerable files - currently the unpacked VLC
    /// engine and cached guide downloads. Deliberately not the roaming directory:
    /// these are large files that must never follow a roaming profile between
    /// machines, and deleting the folder is always safe.
    /// </summary>
    public static string CacheDirectory { get; } = CreateUnder(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Absolute path to a file inside <see cref="DataDirectory"/>.</summary>
    public static string InData(string fileName) => Path.Combine(DataDirectory, fileName);

    /// <summary>Absolute path to a subdirectory of <see cref="DataDirectory"/>, created if missing.</summary>
    public static string EnsureSubDirectory(string name)
    {
        var path = Path.Combine(DataDirectory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Absolute path to a subdirectory of <see cref="CacheDirectory"/>, created if missing.</summary>
    public static string EnsureCacheSubDirectory(string name)
    {
        var path = Path.Combine(CacheDirectory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateUnder(Environment.SpecialFolder folder)
    {
        var root = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create);

        // GetFolderPath returns "" when the platform has no such folder; fall back
        // to the current directory so the app still starts instead of throwing.
        if (string.IsNullOrEmpty(root))
            root = Directory.GetCurrentDirectory();

        var path = Path.Combine(root, AppFolderName);

        if (!Directory.Exists(path))
            TryMigrateLegacy(Path.Combine(root, LegacyAppFolderName), path);

        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Moves a pre-rename directory into place. Best effort on purpose: if the move
    /// fails the app still starts, just with empty settings, which beats refusing to
    /// launch because an old folder was locked.
    /// </summary>
    private static void TryMigrateLegacy(string legacyPath, string newPath)
    {
        try
        {
            if (Directory.Exists(legacyPath))
                Directory.Move(legacyPath, newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
