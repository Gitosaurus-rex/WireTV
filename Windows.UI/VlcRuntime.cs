using System.IO.Compression;
using System.Reflection;
using WireTv.Core.Storage;

namespace WireTv.Windows.UI;

/// <summary>
/// Locates the native VLC engine for the two ways WireTv ships.
///
/// Folder deployment (the default) leaves libvlc.dll and its ~100 MB of codec
/// plugins next to the exe, and LibVLCSharp finds them on its own.
///
/// Single-file deployment cannot work that way: .NET flattens bundled native
/// libraries into a temp directory, which destroys the libvlc\win-x64\plugins
/// layout that VLC requires. So a single-file build embeds the engine as a zip
/// instead, and this class unpacks it once into a cache directory and hands
/// LibVLCSharp an explicit path to it.
/// </summary>
internal static class VlcRuntime
{
    /// <summary>Set by the EmbedLibVlc build target. Absent in a folder deployment.</summary>
    private const string ResourceName = "WireTv.libvlc-win-x64.zip";

    /// <summary>Written only after a complete extraction, so a half-unpacked folder is never trusted.</summary>
    private const string MarkerFileName = ".extracted";

    /// <summary>
    /// Returns the directory holding libvlc.dll, or null when the engine sits
    /// beside the exe and LibVLCSharp should probe for it itself.
    /// </summary>
    public static string? Prepare()
    {
        var assembly = typeof(VlcRuntime).Assembly;

        using var resource = assembly.GetManifestResourceStream(ResourceName);

        if (resource is null)
            return null;

        var target = Path.Combine(AppPaths.CacheDirectory, "runtime", $"libvlc-{ReadEngineVersion(assembly)}");

        if (IsUsable(target))
            return target;

        Extract(resource, target);
        return target;
    }

    private static bool IsUsable(string directory)
        => File.Exists(Path.Combine(directory, MarkerFileName)) &&
           File.Exists(Path.Combine(directory, "libvlc.dll"));

    /// <summary>
    /// Unpacks into a temporary sibling and then moves it into place, so an
    /// interrupted first run cannot leave a partial engine behind that later runs
    /// would treat as good.
    /// </summary>
    private static void Extract(Stream resource, string target)
    {
        var staging = target + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            Directory.CreateDirectory(staging);

            using (var archive = new ZipArchive(resource, ZipArchiveMode.Read))
                archive.ExtractToDirectory(staging, overwriteFiles: true);

            File.WriteAllText(Path.Combine(staging, MarkerFileName), DateTimeOffset.UtcNow.ToString("O"));

            // A previous attempt may have left an incomplete directory here.
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(staging, target);
        }
        catch (IOException) when (IsUsable(target))
        {
            // A second instance won the race and already put a good copy in place.
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>
    /// Version of the embedded engine, stamped in by the build. It names the cache
    /// folder so upgrading the VLC package unpacks afresh instead of mixing
    /// plugins from two versions, which VLC refuses to load.
    /// </summary>
    private static string ReadEngineVersion(Assembly assembly)
    {
        var stamped = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "LibVlcNativeVersion")?
            .Value;

        return string.IsNullOrWhiteSpace(stamped) ? "unknown" : stamped;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Leftover staging costs disk space, nothing more.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
