using WireTv.Core.Models;

namespace WireTv.Core.Epg;

/// <summary>
/// A parsed programme guide, keyed by XMLTV channel id.
///
/// Per-channel schedules are kept sorted by start time so lookups can binary
/// search: the guide view asks "what is on now" for every visible row on every
/// tick, and a linear scan over a few hundred entries per row would show.
/// </summary>
public sealed class EpgGuide
{
    public static readonly EpgGuide Empty = new(
        new Dictionary<string, EpgChannel>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, IReadOnlyList<EpgProgramme>>(StringComparer.OrdinalIgnoreCase));

    private readonly IReadOnlyDictionary<string, IReadOnlyList<EpgProgramme>> _programmes;

    public EpgGuide(
        IReadOnlyDictionary<string, EpgChannel> channels,
        IReadOnlyDictionary<string, IReadOnlyList<EpgProgramme>> programmes)
    {
        Channels = channels;
        _programmes = programmes;
        ProgrammeCount = programmes.Values.Sum(list => list.Count);
    }

    public IReadOnlyDictionary<string, EpgChannel> Channels { get; }

    public int ProgrammeCount { get; }

    /// <summary>Entries dropped during parsing because they fell outside the filter or window.</summary>
    public int SkippedProgrammes { get; init; }

    /// <summary>When this guide was parsed, used to show its age in the UI.</summary>
    public DateTimeOffset LoadedAt { get; init; } = DateTimeOffset.Now;

    public bool IsEmpty => ProgrammeCount == 0;

    /// <summary>Every programme for a channel, in broadcast order.</summary>
    public IReadOnlyList<EpgProgramme> GetSchedule(string? epgChannelId)
        => epgChannelId is not null && _programmes.TryGetValue(epgChannelId, out var list)
            ? list
            : Array.Empty<EpgProgramme>();

    /// <summary>Programmes overlapping a window, for a single day in the guide view.</summary>
    public IReadOnlyList<EpgProgramme> GetSchedule(string? epgChannelId, DateTimeOffset from, DateTimeOffset to)
    {
        var all = GetSchedule(epgChannelId);

        if (all.Count == 0)
            return Array.Empty<EpgProgramme>();

        var result = new List<EpgProgramme>();

        // Start one entry early: the programme in progress at "from" begins before it.
        for (var i = Math.Max(0, IndexAtOrBefore(all, from)); i < all.Count; i++)
        {
            var programme = all[i];

            if (programme.Start >= to)
                break;

            if (programme.Stop > from)
                result.Add(programme);
        }

        return result;
    }

    /// <summary>What is on at a given moment, or null when the guide has no entry covering it.</summary>
    public EpgProgramme? GetAiring(string? epgChannelId, DateTimeOffset moment)
    {
        var all = GetSchedule(epgChannelId);
        var index = IndexAtOrBefore(all, moment);

        if (index < 0)
            return null;

        var candidate = all[index];
        return candidate.IsAiringAt(moment) ? candidate : null;
    }

    /// <summary>The first programme starting after a given moment.</summary>
    public EpgProgramme? GetNext(string? epgChannelId, DateTimeOffset moment)
    {
        var all = GetSchedule(epgChannelId);

        for (var i = Math.Max(0, IndexAtOrBefore(all, moment)); i < all.Count; i++)
        {
            if (all[i].Start > moment)
                return all[i];
        }

        return null;
    }

    /// <summary>Index of the last programme starting at or before <paramref name="moment"/>, or -1.</summary>
    private static int IndexAtOrBefore(IReadOnlyList<EpgProgramme> sorted, DateTimeOffset moment)
    {
        var low = 0;
        var high = sorted.Count - 1;
        var found = -1;

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);

            if (sorted[middle].Start <= moment)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }
}
