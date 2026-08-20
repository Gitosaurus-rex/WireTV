namespace WireTv.Core.Models;

/// <summary>The parsed result of one M3U/M3U8 document.</summary>
public sealed class Playlist
{
    public static readonly Playlist Empty = new()
    {
        Channels = Array.Empty<Channel>()
    };

    /// <summary>Friendly name, normally taken from the source profile.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// EPG URL advertised by the playlist header (url-tvg / x-tvg-url).
    /// Used as a fallback when the profile has no explicit EPG URL.
    /// </summary>
    public string? EpgUrl { get; init; }

    public required IReadOnlyList<Channel> Channels { get; init; }

    /// <summary>Lines the parser could not make sense of. Useful when a provider ships a broken list.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public int Count => Channels.Count;

    /// <summary>Groups in first-seen order, which is the order providers intend.</summary>
    public IReadOnlyList<ChannelGroup> GetGroups()
    {
        var order = new List<string>();
        var buckets = new Dictionary<string, List<Channel>>(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in Channels)
        {
            if (!buckets.TryGetValue(channel.Group, out var bucket))
            {
                bucket = new List<Channel>();
                buckets[channel.Group] = bucket;
                order.Add(channel.Group);
            }

            bucket.Add(channel);
        }

        return order
            .Select(name => new ChannelGroup { Name = name, Channels = buckets[name] })
            .ToArray();
    }
}
