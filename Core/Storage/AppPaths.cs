namespace OpenTv.Core.Storage;

/// <summary>
/// Resolves where OpenTv keeps its user data. Uses SpecialFolder rather than
/// hard-coded Windows paths so an Android/iOS head resolves correctly too.
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "OpenTv";

    /// <summary>Per-user data directory. Created on first access.</summary>
    public static string DataDirectory { get; } = CreateDataDirectory();

    /// <summary>
    /// Machine-local directory for regenerable files - currently the unpacked VLC
    /// engine. Deliberately not the roaming directory: these are large native
    /// binaries that must never follow a roaming profile between machines, and
    /// deleting the folder is always safe.
    /// </summary>
    public static string CacheDirectory { get; } = CreateCacheDirectory();

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

    private static string CreateDataDirectory()
        => CreateUnder(Environment.SpecialFolder.ApplicationData);

    private static string CreateCacheDirectory()
        => CreateUnder(Environment.SpecialFolder.LocalApplicationData);

    private static string CreateUnder(Environment.SpecialFolder folder)
    {
        var root = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create);

        // GetFolderPath returns "" when the platform has no such folder; fall back
        // to the current directory so the app still starts instead of throwing.
        if (string.IsNullOrEmpty(root))
            root = Directory.GetCurrentDirectory();

        var path = Path.Combine(root, AppFolderName);
        Directory.CreateDirectory(path);
        return path;
    }
}
