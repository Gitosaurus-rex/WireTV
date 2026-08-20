namespace WireTv.Core.Vpn;

/// <summary>
/// Routes connect/disconnect requests to the backend that matches the profile
/// and enforces the "at most one tunnel at a time" rule, so the UI only ever has
/// to deal with a single aggregated <see cref="VpnState"/>.
/// </summary>
public sealed class VpnManager : IDisposable
{
    private readonly Dictionary<VpnKind, IVpnService> _backends;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IVpnService? _active;

    public VpnManager(IEnumerable<IVpnService> backends)
    {
        ArgumentNullException.ThrowIfNull(backends);

        _backends = backends.ToDictionary(b => b.Kind);

        foreach (var backend in _backends.Values)
            backend.StateChanged += OnBackendStateChanged;
    }

    /// <summary>Aggregated state: the active backend's state, or Disconnected.</summary>
    public VpnState State { get; private set; } = VpnState.Disconnected;

    /// <summary>The profile the manager last tried to bring up. Null once disconnected.</summary>
    public VpnProfile? ActiveProfile { get; private set; }

    public event EventHandler<VpnState>? StateChanged;

    public IVpnService? GetBackend(VpnKind kind) => _backends.GetValueOrDefault(kind);

    public bool IsSupported(VpnKind kind) => _backends.ContainsKey(kind);

    /// <summary>
    /// Brings up <paramref name="profile"/>, tearing down any other tunnel first.
    /// Failures surface as a Faulted state rather than an exception.
    /// </summary>
    public async Task ConnectAsync(VpnProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (!_backends.TryGetValue(profile.Kind, out var backend))
            {
                Publish(new VpnState(VpnStatus.Faulted, profile.Name,
                    $"{profile.Kind} is not supported on this platform."));
                return;
            }

            if (!backend.IsAvailable)
            {
                Publish(new VpnState(VpnStatus.Faulted, profile.Name,
                    backend.UnavailableReason ?? $"{profile.Kind} is not available on this machine."));
                return;
            }

            // Only one tunnel at a time: two default routes would fight over traffic.
            if (_active is not null && _active != backend)
                await SafeDisconnectAsync(_active, ct).ConfigureAwait(false);

            _active = backend;
            ActiveProfile = profile;

            await backend.ConnectAsync(profile, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (_active is null)
            {
                Publish(VpnState.Disconnected);
                return;
            }

            await SafeDisconnectAsync(_active, ct).ConfigureAwait(false);
            _active = null;
            ActiveProfile = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Asks every backend for its real state. A tunnel started by a previous run of
    /// the app (or by the WireGuard UI itself) is adopted as the active one.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        foreach (var backend in _backends.Values)
        {
            VpnState state;

            try
            {
                state = await backend.RefreshAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                state = new VpnState(VpnStatus.Faulted, null, ex.Message);
            }

            if (state.IsConnected)
            {
                _active = backend;
                Publish(state);
                return;
            }
        }

        if (_active is null)
            Publish(VpnState.Disconnected);
    }

    private static async Task SafeDisconnectAsync(IVpnService backend, CancellationToken ct)
    {
        try
        {
            await backend.DisconnectAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new VpnException($"Could not stop the {backend.Kind} tunnel: {ex.Message}", ex);
        }
    }

    private void OnBackendStateChanged(object? sender, VpnState state)
    {
        // Ignore chatter from a backend that is not the active one, otherwise a
        // stale "disconnected" could overwrite a live tunnel's state.
        if (sender is IVpnService backend && _active is not null && backend != _active)
            return;

        Publish(state);
    }

    private void Publish(VpnState state)
    {
        State = state;

        if (state.Status == VpnStatus.Disconnected)
            ActiveProfile = null;

        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        foreach (var backend in _backends.Values)
        {
            backend.StateChanged -= OnBackendStateChanged;
            backend.Dispose();
        }

        _gate.Dispose();
    }
}
