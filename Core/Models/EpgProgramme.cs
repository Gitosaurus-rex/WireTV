namespace OpenTv.Core.Models;

/// <summary>A single entry in the electronic programme guide.</summary>
public sealed class EpgProgramme
{
    /// <summary>The XMLTV channel id this programme belongs to.</summary>
    public required string ChannelId { get; init; }

    public required string Title { get; init; }

    /// <summary>Episode title, from XMLTV sub-title.</summary>
    public string? SubTitle { get; init; }

    public string? Description { get; init; }

    public string? Category { get; init; }

    /// <summary>Human-readable episode number such as "S2 E4", when the guide supplies one.</summary>
    public string? EpisodeNumber { get; init; }

    public required DateTimeOffset Start { get; init; }

    public required DateTimeOffset Stop { get; init; }

    public TimeSpan Duration => Stop - Start;

    public bool IsAiringAt(DateTimeOffset moment) => moment >= Start && moment < Stop;

    /// <summary>How far the programme has run, 0 to 1. Used for the progress bars in the guide.</summary>
    public double ProgressAt(DateTimeOffset moment)
    {
        var total = Duration.TotalSeconds;

        // Guides do contain zero-length and reversed entries; treat them as finished
        // rather than dividing by zero.
        if (total <= 0)
            return moment >= Stop ? 1 : 0;

        var elapsed = (moment - Start).TotalSeconds;
        return Math.Clamp(elapsed / total, 0, 1);
    }

    public override string ToString() => $"{Start:HH:mm} {Title}";
}

/// <summary>A channel as described by the guide, which is separate from a playlist channel.</summary>
public sealed class EpgChannel
{
    public required string Id { get; init; }

    /// <summary>XMLTV allows several display-name entries, typically one per language.</summary>
    public required IReadOnlyList<string> DisplayNames { get; init; }

    public string? IconUrl { get; init; }

    public string PrimaryName => DisplayNames.Count > 0 ? DisplayNames[0] : Id;

    public override string ToString() => PrimaryName;
}
