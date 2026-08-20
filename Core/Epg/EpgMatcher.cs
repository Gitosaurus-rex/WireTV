using System.Globalization;
using System.Text;
using OpenTv.Core.Models;

namespace OpenTv.Core.Epg;

/// <summary>
/// Pairs playlist channels with guide channels.
///
/// A well-behaved provider sets tvg-id on every channel and the job is a
/// dictionary lookup. In practice tvg-id is often missing or wrong, so there is a
/// name-based fallback that strips the decorations IPTV lists carry - country
/// prefixes like "SE:" or "|SE|", and quality suffixes like "HD" or "4K" - which
/// exist in the playlist name but never in the guide.
/// </summary>
public static class EpgMatcher
{
    private static readonly string[] QualityTokens =
    [
        "uhd", "fhd", "hd", "sd", "4k", "8k", "1080p", "1080", "720p", "720", "hevc", "h265", "raw", "backup"
    ];

    /// <summary>
    /// Channel ids worth keeping when parsing, given the playlist in hand. Passed to
    /// <see cref="XmltvParseOptions.SelectChannels"/> so unrelated channels are
    /// discarded while reading rather than held in memory.
    /// </summary>
    public static IReadOnlySet<string> SelectRelevantChannelIds(
        IReadOnlyCollection<EpgChannel> guideChannels,
        IReadOnlyList<Channel> playlistChannels)
    {
        ArgumentNullException.ThrowIfNull(guideChannels);
        ArgumentNullException.ThrowIfNull(playlistChannels);

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byNormalisedName = BuildNameIndex(guideChannels);
        var knownIds = guideChannels.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in playlistChannels)
        {
            if (Resolve(channel, knownIds, byNormalisedName) is { } id)
                wanted.Add(id);
        }

        return wanted;
    }

    /// <summary>Maps playlist channel id to guide channel id for the channels that matched.</summary>
    public static IReadOnlyDictionary<string, string> Match(
        EpgGuide guide,
        IReadOnlyList<Channel> playlistChannels)
    {
        ArgumentNullException.ThrowIfNull(guide);
        ArgumentNullException.ThrowIfNull(playlistChannels);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var byNormalisedName = BuildNameIndex(guide.Channels.Values);
        var knownIds = guide.Channels.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in playlistChannels)
        {
            if (Resolve(channel, knownIds, byNormalisedName) is { } id)
                result[channel.Id] = id;
        }

        return result;
    }

    private static string? Resolve(
        Channel channel,
        IReadOnlySet<string> knownIds,
        IReadOnlyDictionary<string, string> byNormalisedName)
    {
        // tvg-id is authoritative when the guide actually knows it.
        if (!string.IsNullOrWhiteSpace(channel.TvgId) && knownIds.Contains(channel.TvgId))
            return channel.TvgId;

        foreach (var candidate in new[] { channel.TvgName, channel.Name, channel.TvgId })
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var normalised = Normalise(candidate);

            if (normalised.Length > 0 && byNormalisedName.TryGetValue(normalised, out var id))
                return id;
        }

        return null;
    }

    /// <summary>
    /// Normalised display name to guide channel id. First writer wins, so an earlier
    /// channel is not silently replaced by a later one with the same reduced name.
    /// </summary>
    private static Dictionary<string, string> BuildNameIndex(IEnumerable<EpgChannel> guideChannels)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var channel in guideChannels)
        {
            foreach (var name in channel.DisplayNames.Append(channel.Id))
            {
                var normalised = Normalise(name);

                if (normalised.Length > 0)
                    index.TryAdd(normalised, channel.Id);
            }
        }

        return index;
    }

    /// <summary>
    /// Reduces a channel name to a comparable core: lower case, no accents, no
    /// punctuation, no country prefix and no quality suffix. "SE: SVT 1 HD" and
    /// "svt1" both come out as "svt1".
    /// </summary>
    public static string Normalise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var text = StripCountryPrefix(name.Trim());

        // Strip accents so "Kanal 5" and "Kanál 5" compare equal.
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
            else if (builder.Length > 0 && builder[^1] != ' ')
                builder.Append(' ');
        }

        var words = builder.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        // Quality markers only ever appear on the playlist side.
        while (words.Count > 1 && QualityTokens.Contains(words[^1], StringComparer.Ordinal))
            words.RemoveAt(words.Count - 1);

        // Joining without separators makes "svt 1" and "svt1" identical.
        return string.Concat(words);
    }

    /// <summary>
    /// Removes the country tag IPTV lists prefix channel names with.
    ///
    /// Bracketed forms ("|SE| SVT1", "[US] CNN") are unambiguous. The bare colon
    /// form is only stripped for a two-letter code, so a channel genuinely called
    /// "MTV: Hits" keeps its name instead of being reduced to "hits".
    /// </summary>
    private static string StripCountryPrefix(string text)
    {
        if (text.Length == 0)
            return text;

        var closing = text[0] switch
        {
            '|' => '|',
            '[' => ']',
            '(' => ')',
            _ => '\0'
        };

        if (closing != '\0')
        {
            var end = text.IndexOf(closing, 1);

            if (end is > 0 and <= 6)
                return text[(end + 1)..];
        }

        if (text.Length > 3 && text[2] == ':' && char.IsLetter(text[0]) && char.IsLetter(text[1]))
            return text[3..];

        return text;
    }
}
