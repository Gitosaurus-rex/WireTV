using System.Text.Json;
using System.Text.Json.Serialization;

namespace WireTv.Core.Storage;

/// <summary>
/// Small JSON-file backed store for a single settings document.
/// Writes go through a temp file so a crash mid-write cannot leave the user
/// with a truncated settings file.
/// </summary>
public sealed class JsonStore<T> where T : class, new()
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonStore(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
    }

    public string FilePath => _filePath;

    /// <summary>Loads the document, returning a fresh instance when the file is missing or unreadable.</summary>
    public async Task<T> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (!File.Exists(_filePath))
                return new T();

            await using var stream = File.OpenRead(_filePath);
            var value = await JsonSerializer.DeserializeAsync<T>(stream, Options, ct).ConfigureAwait(false);
            return value ?? new T();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt settings file must not prevent the app from starting.
            // Keep the bad file around so it can be inspected, and start clean.
            TryBackupCorruptFile();
            return new T();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(T value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var tempPath = _filePath + ".tmp";

            await using (var stream = File.Create(tempPath))
                await JsonSerializer.SerializeAsync(stream, value, Options, ct).ConfigureAwait(false);

            // File.Move with overwrite is atomic enough for our purposes on NTFS.
            File.Move(tempPath, _filePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            if (File.Exists(_filePath))
                File.Move(_filePath, _filePath + ".corrupt", overwrite: true);
        }
        catch
        {
            // Best effort only - never let cleanup break startup.
        }
    }
}
