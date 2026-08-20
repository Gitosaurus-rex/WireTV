namespace OpenTv.Core.Vpn;

public enum VpnKind
{
    WireGuard,
    OpenVpn
}

public enum VpnStatus
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,

    /// <summary>The last operation failed; <see cref="VpnState.Message"/> explains why.</summary>
    Faulted
}

/// <summary>
/// A snapshot of one backend's tunnel state. Immutable so it can be handed to
/// the UI thread without further synchronisation.
/// </summary>
public sealed record VpnState(
    VpnStatus Status,
    string? ProfileName = null,
    string? Message = null,
    DateTimeOffset? ConnectedSince = null)
{
    public static readonly VpnState Disconnected = new(VpnStatus.Disconnected);

    public bool IsConnected => Status == VpnStatus.Connected;

    public bool IsBusy => Status is VpnStatus.Connecting or VpnStatus.Disconnecting;
}

/// <summary>A VPN configuration the user has imported into OpenTv.</summary>
public sealed class VpnProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>User-facing name. Also seeds the tunnel/service name on Windows.</summary>
    public string Name { get; set; } = string.Empty;

    public VpnKind Kind { get; set; } = VpnKind.WireGuard;

    /// <summary>
    /// Absolute path to the config file OpenTv owns. Import copies the user's
    /// original file here so later edits or deletions cannot break the profile.
    /// </summary>
    public string ConfigPath { get; set; } = string.Empty;

    public DateTimeOffset ImportedUtc { get; set; } = DateTimeOffset.UtcNow;

    public override string ToString() => Name;
}

/// <summary>Raised when a tunnel operation fails in a way worth showing to the user.</summary>
public sealed class VpnException : Exception
{
    public VpnException(string message) : base(message) { }

    public VpnException(string message, Exception inner) : base(message, inner) { }
}
