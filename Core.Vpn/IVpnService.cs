namespace WireTv.Core.Vpn;

/// <summary>
/// One VPN backend (WireGuard or OpenVPN) on one platform.
///
/// Implementations must never throw from <see cref="ConnectAsync"/> for ordinary
/// failures; they report them via <see cref="State"/> with
/// <see cref="VpnStatus.Faulted"/> and a human-readable message, and raise
/// <see cref="StateChanged"/> so the UI can react.
/// </summary>
public interface IVpnService : IDisposable
{
    VpnKind Kind { get; }

    /// <summary>Current snapshot. Never null.</summary>
    VpnState State { get; }

    /// <summary>
    /// Raised on every state transition. May fire from a background thread, so
    /// UI consumers must marshal to their dispatcher.
    /// </summary>
    event EventHandler<VpnState>? StateChanged;

    /// <summary>False when the required client software is missing on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>Explains why <see cref="IsAvailable"/> is false, otherwise null.</summary>
    string? UnavailableReason { get; }

    /// <summary>Brings up the tunnel. Completes once the tunnel is up or has failed.</summary>
    Task ConnectAsync(VpnProfile profile, CancellationToken ct = default);

    /// <summary>Tears down whatever tunnel this backend currently owns.</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// Re-reads the real tunnel state from the platform. Used at startup, because a
    /// tunnel can outlive the app process, and periodically to catch external changes.
    /// </summary>
    Task<VpnState> RefreshAsync(CancellationToken ct = default);
}
