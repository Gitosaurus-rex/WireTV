namespace OpenTv.Core.Models;

/// <summary>A named bucket of channels, derived from group-title / #EXTGRP.</summary>
public sealed class ChannelGroup
{
    /// <summary>Group assigned to channels the provider left ungrouped.</summary>
    public const string Ungrouped = "Ungrouped";

    /// <summary>Pseudo-group used by the UI filter to mean "no filtering".</summary>
    public const string AllChannels = "All channels";

    public required string Name { get; init; }
    public required IReadOnlyList<Channel> Channels { get; init; }

    public int Count => Channels.Count;

    public override string ToString() => $"{Name} ({Count})";
}
