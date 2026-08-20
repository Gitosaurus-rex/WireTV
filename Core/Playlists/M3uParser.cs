using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WireTv.Core.Models;

namespace WireTv.Core.Playlists;

/// <summary>
/// Parser for extended M3U playlists as shipped by IPTV providers.
///
/// Deliberately forgiving: real provider playlists routinely contain stray blank
/// lines, unknown #-directives, missing attributes and duplicated entries. A
/// single malformed entry must never abort the import, so problems are collected
/// as warnings and parsing continues.
/// </summary>
public static class M3uParser
{
    private static readonly Regex AttributeRegex = new(
        "(?<key>[A-Za-z0-9_-]+)\\s*=\\s*\"(?<value>[^\"]*)\"",
        RegexOptions.Compiled);

    /// <summary>Caps the warning list so a totally broken file cannot exhaust memory.</summary>
    private const int MaxWarnings = 100;

    public static Playlist Parse(string content, string? name = null)
    {
        using var reader = new StringReader(content);
        return Parse(reader, name);
    }

    public static Playlist Parse(TextReader reader, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var channels = new List<Channel>();
        var warnings = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        string? headerEpgUrl = null;
        string? currentGroupDirective = null;

        // State for the entry currently being assembled.
        PendingEntry? pending = null;

        var lineNumber = 0;
        string? rawLine;

        while ((rawLine = reader.ReadLine()) is not null)
        {
            lineNumber++;
            var line = rawLine.Trim();

            if (line.Length == 0)
                continue;

            if (line[0] == '#')
            {
                if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                {
                    headerEpgUrl ??= ReadHeaderEpgUrl(line);
                }
                else if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    if (pending is not null)
                        AddWarning(warnings, $"Line {pending.LineNumber}: #EXTINF without a following stream URL, entry skipped.");

                    pending = ParseExtInf(line, lineNumber, currentGroupDirective, warnings);
                }
                else if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
                {
                    // #EXTGRP applies to entries that follow it, and may appear either
                    // before or after the #EXTINF line of the entry it belongs to.
                    var group = line["#EXTGRP:".Length..].Trim();
                    currentGroupDirective = group.Length == 0 ? null : group;

                    if (pending is not null && pending.Group is null)
                        pending.Group = currentGroupDirective;
                }
                else if (line.StartsWith("#EXTVLCOPT:", StringComparison.OrdinalIgnoreCase))
                {
                    var option = line["#EXTVLCOPT:".Length..].Trim();
                    if (option.Length > 0)
                        (pending ??= PendingEntry.Anonymous(lineNumber)).Options.Add(":" + option);
                }

                // Everything else (#KODIPROP, #EXTIMG, plain comments, ...) is ignored on purpose.
                continue;
            }

            // A non-# line is the stream URL closing the pending entry.
            if (pending is null)
            {
                AddWarning(warnings, $"Line {lineNumber}: stream URL without a preceding #EXTINF, entry skipped.");
                continue;
            }

            channels.Add(pending.ToChannel(line, seenIds));
            pending = null;
        }

        if (pending is not null)
            AddWarning(warnings, $"Line {pending.LineNumber}: #EXTINF without a following stream URL, entry skipped.");

        return new Playlist
        {
            Name = name,
            EpgUrl = headerEpgUrl,
            Channels = channels,
            Warnings = warnings
        };
    }

    private static void AddWarning(List<string> warnings, string message)
    {
        if (warnings.Count < MaxWarnings)
            warnings.Add(message);
        else if (warnings.Count == MaxWarnings)
            warnings.Add("Further warnings suppressed.");
    }

    /// <summary>Reads url-tvg / x-tvg-url from the #EXTM3U header line.</summary>
    private static string? ReadHeaderEpgUrl(string headerLine)
    {
        var attributes = ReadAttributes(headerLine);

        foreach (var key in new[] { "url-tvg", "x-tvg-url", "tvg-url" })
        {
            if (!attributes.TryGetValue(key, out var value) || value.Length == 0)
                continue;

            // Providers sometimes list several comma-separated guides; take the first.
            var first = value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(first))
                return first;
        }

        return null;
    }

    private static PendingEntry ParseExtInf(string line, int lineNumber, string? groupDirective, List<string> warnings)
    {
        var payload = line["#EXTINF:".Length..];

        // Split the payload at the first comma that is not inside a quoted
        // attribute value; everything after it is the display name.
        var splitIndex = FindTitleSeparator(payload);

        string metadata;
        string title;

        if (splitIndex < 0)
        {
            metadata = payload;
            title = string.Empty;
            AddWarning(warnings, $"Line {lineNumber}: #EXTINF has no display name.");
        }
        else
        {
            metadata = payload[..splitIndex];
            title = payload[(splitIndex + 1)..].Trim();
        }

        var attributes = ReadAttributes(metadata);

        var entry = new PendingEntry(lineNumber)
        {
            Duration = ReadDuration(metadata),
            Attributes = attributes,
            Group = Pick(attributes, "group-title") ?? groupDirective,
            TvgId = Pick(attributes, "tvg-id"),
            TvgName = Pick(attributes, "tvg-name"),
            LogoUrl = Pick(attributes, "tvg-logo") ?? Pick(attributes, "logo"),
            Number = ReadNumber(attributes)
        };

        // Fall back to tvg-name when the provider left the display name empty.
        entry.Title = title.Length > 0 ? title : entry.TvgName ?? $"Channel {lineNumber}";

        return entry;
    }

    /// <summary>Index of the comma separating attributes from the display name, or -1.</summary>
    private static int FindTitleSeparator(string payload)
    {
        var inQuotes = false;

        for (var i = 0; i < payload.Length; i++)
        {
            var c = payload[i];

            if (c == '"')
                inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
                return i;
        }

        return -1;
    }

    private static double ReadDuration(string metadata)
    {
        var span = metadata.AsSpan().TrimStart();
        var end = 0;

        while (end < span.Length && (char.IsDigit(span[end]) || span[end] is '-' or '.' or '+'))
            end++;

        return double.TryParse(span[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            ? duration
            : -1;
    }

    private static int? ReadNumber(IReadOnlyDictionary<string, string> attributes)
    {
        foreach (var key in new[] { "tvg-chno", "channel-number", "tvg-num" })
        {
            if (attributes.TryGetValue(key, out var raw) &&
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return number;
            }
        }

        return null;
    }

    private static string? Pick(IReadOnlyDictionary<string, string> attributes, string key)
        => attributes.TryGetValue(key, out var value) && value.Trim().Length > 0 ? value.Trim() : null;

    private static Dictionary<string, string> ReadAttributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in AttributeRegex.Matches(text))
            result[match.Groups["key"].Value] = match.Groups["value"].Value;

        return result;
    }

    /// <summary>Mutable scratch state for the entry currently being assembled.</summary>
    private sealed class PendingEntry(int lineNumber)
    {
        public int LineNumber { get; } = lineNumber;
        public string Title { get; set; } = string.Empty;
        public double Duration { get; set; } = -1;
        public string? Group { get; set; }
        public string? TvgId { get; set; }
        public string? TvgName { get; set; }
        public string? LogoUrl { get; set; }
        public int? Number { get; set; }
        public List<string> Options { get; } = new();

        public IReadOnlyDictionary<string, string> Attributes { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>An entry that only ever received #EXTVLCOPT lines, no #EXTINF.</summary>
        public static PendingEntry Anonymous(int lineNumber) => new(lineNumber)
        {
            Title = $"Channel {lineNumber}"
        };

        public Channel ToChannel(string streamUrl, HashSet<string> seenIds)
        {
            var title = Title.Length > 0 ? Title : $"Channel {LineNumber}";

            return new Channel
            {
                Id = BuildUniqueId(title, streamUrl, seenIds),
                Name = title,
                StreamUrl = streamUrl,
                TvgId = TvgId,
                TvgName = TvgName,
                LogoUrl = LogoUrl,
                Group = Group ?? ChannelGroup.Ungrouped,
                Number = Number,
                Duration = Duration,
                PlayerOptions = Options.Count == 0 ? Array.Empty<string>() : Options.ToArray(),
                Attributes = Attributes
            };
        }

        /// <summary>
        /// Ids must be stable across refreshes (favourites depend on it) and unique
        /// within one playlist (duplicate entries are common), so the id is derived
        /// from content and only suffixed when an actual collision occurs.
        /// </summary>
        private string BuildUniqueId(string title, string streamUrl, HashSet<string> seenIds)
        {
            var seed = !string.IsNullOrWhiteSpace(TvgId) ? TvgId! : $"{title}|{streamUrl}";

            var baseId = StableId.Hash(seed);
            var id = baseId;
            var suffix = 1;

            while (!seenIds.Add(id))
                id = $"{baseId}-{suffix++}";

            return id;
        }
    }
}

/// <summary>Deterministic short hash. Not security relevant; identity only.</summary>
internal static class StableId
{
    public static string Hash(string value)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }
}
