namespace OpenTv.Core.Models;

/// <summary>
/// A single playable entry parsed from a playlist.
/// Immutable: playlists are rebuilt on refresh rather than mutated in place.
/// </summary>
public sealed class Channel
{
    /// <summary>Stable identity used for favourites, EPG matching and resume state.</summary>
    public required string Id { get; init; }

    /// <summary>Display name (the text after the comma on the #EXTINF line).</summary>
    public required string Name { get; init; }

    /// <summary>Absolute stream URL.</summary>
    public required string StreamUrl { get; init; }

    /// <summary>XMLTV channel id from tvg-id, when the provider supplies one.</summary>
    public string? TvgId { get; init; }

    /// <summary>Provider's canonical channel name from tvg-name.</summary>
    public string? TvgName { get; init; }

    /// <summary>Logo URL from tvg-logo.</summary>
    public string? LogoUrl { get; init; }

    /// <summary>Group from group-title or a preceding #EXTGRP line.</summary>
    public string Group { get; init; } = ChannelGroup.Ungrouped;

    /// <summary>Channel number from tvg-chno / channel-number, when present.</summary>
    public int? Number { get; init; }

    /// <summary>#EXTINF duration. -1 for live streams, which is the normal case.</summary>
    public double Duration { get; init; } = -1;

    /// <summary>
    /// Per-channel playback options collected from #EXTVLCOPT lines, already in
    /// ":key=value" form. The player layer decides whether it can honour them.
    /// </summary>
    public IReadOnlyList<string> PlayerOptions { get; init; } = Array.Empty<string>();

    /// <summary>Every raw key="value" attribute from the #EXTINF line, unmodified.</summary>
    public IReadOnlyDictionary<string, string> Attributes { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public override string ToString() => Name;
}
