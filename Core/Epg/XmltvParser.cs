using System.Globalization;
using System.Text;
using System.Xml;
using WireTv.Core.Models;

namespace WireTv.Core.Epg;

/// <summary>Controls how much of a guide is kept, so a large XMLTV file does not become a large heap.</summary>
public sealed class XmltvParseOptions
{
    public static readonly XmltvParseOptions Default = new();

    /// <summary>
    /// Called once, after all channel elements have been read and before the first
    /// programme, to decide which channel ids are worth keeping. Returning null keeps
    /// everything. Providers routinely ship guides covering thousands of channels the
    /// user is not subscribed to, and dropping those early is the single biggest win.
    /// </summary>
    public Func<IReadOnlyCollection<EpgChannel>, IReadOnlySet<string>?>? SelectChannels { get; init; }

    /// <summary>Programmes that finished before this are dropped. Null keeps all history.</summary>
    public DateTimeOffset? DiscardBefore { get; init; }

    /// <summary>Programmes starting after this are dropped. Null keeps the whole future.</summary>
    public DateTimeOffset? DiscardAfter { get; init; }

    /// <summary>Offset assumed when a timestamp carries none. Defaults to the machine's.</summary>
    public TimeSpan? AssumedOffset { get; init; }

    /// <summary>Safety valve against a malformed or hostile guide.</summary>
    public int MaxProgrammes { get; init; } = 1_000_000;
}

/// <summary>
/// Streaming XMLTV reader.
///
/// Guides are commonly 50-200 MB of XML, so this reads forward only and never
/// builds a DOM. Channel elements come before programme elements in every XMLTV
/// file, which is what makes the single-pass filtering in
/// <see cref="XmltvParseOptions.SelectChannels"/> possible.
///
/// Navigation note: every Read* helper below leaves the reader positioned on the
/// node *after* the element it consumed, and the loops therefore never call
/// Read() again on their own. Mixing the two conventions silently skips every
/// other sibling, which is easy to do and hard to spot.
/// </summary>
public static class XmltvParser
{
    public static EpgGuide Parse(Stream stream, XmltvParseOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= XmltvParseOptions.Default;

        var settings = new XmlReaderSettings
        {
            // Remote guides are untrusted input: no DTDs, no external entity
            // resolution, so a hostile file cannot read local files or hang us.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            IgnoreProcessingInstructions = true,
            CloseInput = false
        };

        var assumedOffset = options.AssumedOffset ?? DateTimeOffset.Now.Offset;

        var channels = new Dictionary<string, EpgChannel>(StringComparer.OrdinalIgnoreCase);
        var programmes = new Dictionary<string, List<EpgProgramme>>(StringComparer.OrdinalIgnoreCase);

        IReadOnlySet<string>? wanted = null;
        var filterResolved = false;
        var skipped = 0;
        var kept = 0;

        using var reader = XmlReader.Create(stream, settings);
        reader.Read();

        while (!reader.EOF)
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            if (reader.Name.Equals("channel", StringComparison.OrdinalIgnoreCase))
            {
                if (ReadChannel(reader) is { } channel)
                    channels[channel.Id] = channel;

                continue;
            }

            if (reader.Name.Equals("programme", StringComparison.OrdinalIgnoreCase))
            {
                if (!filterResolved)
                {
                    wanted = options.SelectChannels?.Invoke(channels.Values);
                    filterResolved = true;
                }

                if (kept >= options.MaxProgrammes)
                    break;

                var programme = ReadProgramme(reader, assumedOffset, wanted, options);

                if (programme is null)
                {
                    skipped++;
                    continue;
                }

                if (!programmes.TryGetValue(programme.ChannelId, out var list))
                {
                    list = new List<EpgProgramme>();
                    programmes[programme.ChannelId] = list;
                }

                list.Add(programme);
                kept++;
                continue;
            }

            // Descend into the <tv> wrapper; skip any other element wholesale.
            if (reader.Name.Equals("tv", StringComparison.OrdinalIgnoreCase))
                reader.Read();
            else
                reader.Skip();
        }

        // Lookups rely on ordering, and guides are not reliably sorted.
        foreach (var list in programmes.Values)
            list.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        return new EpgGuide(channels, programmes.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<EpgProgramme>)pair.Value,
            StringComparer.OrdinalIgnoreCase))
        {
            SkippedProgrammes = skipped
        };
    }

    private static EpgChannel? ReadChannel(XmlReader reader)
    {
        var id = reader.GetAttribute("id");
        var displayNames = new List<string>();
        string? iconUrl = null;

        if (reader.IsEmptyElement)
        {
            reader.Read();
        }
        else
        {
            var depth = reader.Depth;
            reader.Read();

            while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }

                // Every branch consumes its element, so the loop always progresses.
                switch (reader.Name.ToLowerInvariant())
                {
                    case "display-name":
                        var name = ReadElementText(reader);
                        if (!string.IsNullOrWhiteSpace(name))
                            displayNames.Add(name.Trim());
                        break;

                    case "icon":
                        iconUrl ??= reader.GetAttribute("src");
                        reader.Skip();
                        break;

                    default:
                        reader.Skip();
                        break;
                }
            }

            if (!reader.EOF)
                reader.Read();
        }

        if (string.IsNullOrWhiteSpace(id))
            return null;

        return new EpgChannel
        {
            Id = id.Trim(),
            DisplayNames = displayNames,
            IconUrl = string.IsNullOrWhiteSpace(iconUrl) ? null : iconUrl
        };
    }

    private static EpgProgramme? ReadProgramme(
        XmlReader reader,
        TimeSpan assumedOffset,
        IReadOnlySet<string>? wanted,
        XmltvParseOptions options)
    {
        var channelId = reader.GetAttribute("channel");

        if (!string.IsNullOrWhiteSpace(channelId) &&
            (wanted is null || wanted.Contains(channelId)) &&
            TryParseTimestamp(reader.GetAttribute("start"), assumedOffset, out var start))
        {
            // A missing stop is legal in XMLTV; assume an hour so the entry still shows.
            if (!TryParseTimestamp(reader.GetAttribute("stop"), assumedOffset, out var stop) || stop <= start)
                stop = start.AddHours(1);

            var insideWindow = (options.DiscardBefore is not { } floor || stop > floor) &&
                               (options.DiscardAfter is not { } ceiling || start < ceiling);

            if (insideWindow)
                return ReadProgrammeBody(reader, channelId.Trim(), start, stop);
        }

        reader.Skip();
        return null;
    }

    private static EpgProgramme ReadProgrammeBody(
        XmlReader reader,
        string channelId,
        DateTimeOffset start,
        DateTimeOffset stop)
    {
        string? title = null;
        string? subTitle = null;
        string? description = null;
        string? category = null;
        string? episode = null;

        if (reader.IsEmptyElement)
        {
            reader.Read();
        }
        else
        {
            var depth = reader.Depth;
            reader.Read();

            while (!reader.EOF && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                    continue;
                }

                // Guides repeat these once per language and the first one wins. The
                // text is always read first: writing "title ??= ReadElementText(...)"
                // would skip the call once title is set, leaving the reader parked on
                // the element and looping forever.
                switch (reader.Name.ToLowerInvariant())
                {
                    case "title":
                        var titleText = ReadElementText(reader);
                        title ??= titleText;
                        break;

                    case "sub-title":
                        var subTitleText = ReadElementText(reader);
                        subTitle ??= subTitleText;
                        break;

                    case "desc":
                        var descriptionText = ReadElementText(reader);
                        description ??= descriptionText;
                        break;

                    case "category":
                        var categoryText = ReadElementText(reader);
                        category ??= categoryText;
                        break;

                    case "episode-num":
                        var system = reader.GetAttribute("system");
                        var episodeText = FormatEpisode(system, ReadElementText(reader));
                        episode ??= episodeText;
                        break;

                    default:
                        reader.Skip();
                        break;
                }
            }

            if (!reader.EOF)
                reader.Read();
        }

        return new EpgProgramme
        {
            ChannelId = channelId,
            Title = string.IsNullOrWhiteSpace(title) ? "(no title)" : title.Trim(),
            SubTitle = Clean(subTitle),
            Description = Clean(description),
            Category = Clean(category),
            EpisodeNumber = Clean(episode),
            Start = start,
            Stop = stop
        };
    }

    /// <summary>
    /// Text content of the current element, always advancing past it.
    ///
    /// Written by hand rather than with ReadElementContentAsString, which throws on
    /// an element containing markup - and a guide with a stray tag inside a
    /// description should lose that description, not the whole file.
    /// </summary>
    private static string? ReadElementText(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            reader.Read();
            return null;
        }

        var depth = reader.Depth;
        var builder = new StringBuilder();

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                reader.Read();
                break;
            }

            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
                builder.Append(reader.Value);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Turns an episode-num value into something displayable. The xmltv_ns system
    /// is zero-based and dot-separated ("1.3." meaning season 2, episode 4);
    /// anything else is shown as the guide wrote it.
    /// </summary>
    private static string? FormatEpisode(string? system, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (!string.Equals(system, "xmltv_ns", StringComparison.OrdinalIgnoreCase))
            return value.Trim();

        var parts = value.Split('.');

        if (parts.Length < 2)
            return value.Trim();

        var season = ParseZeroBased(parts[0]);
        var episode = ParseZeroBased(parts[1]);

        return (season, episode) switch
        {
            (not null, not null) => $"S{season} E{episode}",
            (null, not null) => $"E{episode}",
            (not null, null) => $"S{season}",
            _ => null
        };

        static int? ParseZeroBased(string part)
        {
            // Entries may be a range such as "2/6"; only the first number matters here.
            var head = part.Split('/')[0].Trim();

            return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number + 1
                : null;
        }
    }

    /// <summary>
    /// Parses an XMLTV timestamp: 14 digits plus an optional " +HHMM" offset.
    /// Shorter forms are legal too, so the digits are padded before parsing.
    /// </summary>
    public static bool TryParseTimestamp(string? value, TimeSpan assumedOffset, out DateTimeOffset result)
    {
        result = default;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var span = value.AsSpan().Trim();
        var digitCount = 0;

        while (digitCount < span.Length && char.IsAsciiDigit(span[digitCount]))
            digitCount++;

        if (digitCount < 4)
            return false;

        // "20260820" -> "20260820000000", "202608201830" -> "20260820183000"
        Span<char> stamp = stackalloc char[14];
        "00000101000000".AsSpan().CopyTo(stamp);
        span[..Math.Min(digitCount, 14)].CopyTo(stamp);

        if (!DateTime.TryParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var naive))
        {
            return false;
        }

        var offset = assumedOffset;
        var rest = span[digitCount..].Trim();

        if (rest.Length >= 5 && (rest[0] == '+' || rest[0] == '-') &&
            int.TryParse(rest.Slice(1, 2), out var hours) &&
            int.TryParse(rest.Slice(3, 2), out var minutes))
        {
            var magnitude = new TimeSpan(hours, minutes, 0);
            offset = rest[0] == '-' ? -magnitude : magnitude;
        }

        // Offsets outside this range are invalid and would throw.
        if (offset < TimeSpan.FromHours(-14) || offset > TimeSpan.FromHours(14))
            offset = TimeSpan.Zero;

        result = new DateTimeOffset(naive, offset);
        return true;
    }
}
