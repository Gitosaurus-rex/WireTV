using OpenTv.Core.Models;

namespace OpenTv.Core.Storage;

/// <summary>Root document persisted to sources.json.</summary>
public sealed class PlaylistSourceDocument
{
    public List<PlaylistSource> Sources { get; set; } = new();

    /// <summary>Id of the source that was active when the app last closed.</summary>
    public string? LastUsedSourceId { get; set; }

    /// <summary>Id of the channel that was playing when the app last closed.</summary>
    public string? LastChannelId { get; set; }

    public int Volume { get; set; } = 80;
}

/// <summary>
/// Persists the user's provider profiles.
///
/// Xtream passwords are run through an <see cref="ISecretProtector"/> on the way to
/// disk, so sources.json never holds a readable subscription password.
/// </summary>
public sealed class PlaylistSourceStore
{
    public const string FileName = "sources.json";

    private readonly JsonStore<PlaylistSourceDocument> _store;
    private readonly ISecretProtector _protector;

    public PlaylistSourceStore(string? filePath = null, ISecretProtector? protector = null)
    {
        _store = new JsonStore<PlaylistSourceDocument>(filePath ?? AppPaths.InData(FileName));
        _protector = protector ?? PlaintextSecretProtector.Instance;
    }

    public string FilePath => _store.FilePath;

    public async Task<PlaylistSourceDocument> LoadAsync(CancellationToken ct = default)
    {
        var document = await _store.LoadAsync(ct).ConfigureAwait(false);

        // Decrypt in place: callers work with usable credentials throughout.
        foreach (var source in document.Sources)
            source.Password = _protector.Unprotect(source.Password);

        return document;
    }

    public Task SaveAsync(PlaylistSourceDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Serialize a copy so the caller keeps holding plaintext it can still use;
        // encrypting the live objects would corrupt them on the next save.
        var toWrite = new PlaylistSourceDocument
        {
            LastUsedSourceId = document.LastUsedSourceId,
            LastChannelId = document.LastChannelId,
            Volume = document.Volume,
            Sources = document.Sources.Select(CopyWithProtectedPassword).ToList()
        };

        return _store.SaveAsync(toWrite, ct);
    }

    private PlaylistSource CopyWithProtectedPassword(PlaylistSource source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Kind = source.Kind,
        Location = source.Location,
        Username = source.Username,
        Password = _protector.Protect(source.Password),
        EpgUrl = source.EpgUrl,
        LastRefreshedUtc = source.LastRefreshedUtc,
        LastChannelCount = source.LastChannelCount
    };
}
