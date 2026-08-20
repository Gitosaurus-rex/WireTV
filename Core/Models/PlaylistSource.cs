namespace WireTv.Core.Models;

public enum PlaylistSourceKind
{
    M3uUrl,
    M3uFile,

    /// <summary>Xtream Codes provider. Reserved for iteration 2.</summary>
    Xtream
}

/// <summary>
/// A saved provider profile. Mutable and JSON-friendly because the settings
/// screen edits these directly.
/// </summary>
public sealed class PlaylistSource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "New playlist";

    public PlaylistSourceKind Kind { get; set; } = PlaylistSourceKind.M3uUrl;

    /// <summary>HTTP(S) URL for M3uUrl/Xtream, absolute file path for M3uFile.</summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>Xtream credentials. Unused for plain M3U sources.</summary>
    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>Explicit XMLTV URL. Overrides whatever the playlist header advertises.</summary>
    public string? EpgUrl { get; set; }

    public DateTimeOffset? LastRefreshedUtc { get; set; }

    public int LastChannelCount { get; set; }

    public override string ToString() => Name;
}
