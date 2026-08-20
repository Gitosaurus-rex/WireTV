using System.Runtime.Versioning;
using System.ServiceProcess;
using WireTv.Core.Vpn;

namespace WireTv.Windows.Vpn;

/// <summary>
/// Drives the official WireGuard for Windows tunnel service.
///
/// Connecting means running "wireguard.exe /installtunnelservice &lt;config&gt;", which
/// registers and starts a Windows service called WireGuardTunnel$&lt;name&gt;; disconnecting
/// runs "/uninstalltunnelservice &lt;name&gt;". Both need administrator rights, so they go
/// through <see cref="WindowsElevation"/> and produce a UAC prompt.
///
/// The tunnel name is the config file's base name - that is how wireguard.exe derives
/// it - so <see cref="WireGuardProfileImporter"/> makes sure imported files are named
/// with a valid, unique tunnel name.
///
/// A tunnel deliberately outlives the app process: quitting WireTv does not tear the
/// VPN down, which avoids dropping the user onto their bare connection unannounced.
/// <see cref="RefreshAsync"/> re-adopts a tunnel that is already up at startup.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WireGuardVpnService : IVpnService
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(5);

    private const string ServicePrefix = "WireGuardTunnel$";

    private readonly string? _executable;
    private readonly Timer _watchdog;

    private string? _tunnelName;
    private VpnState _state = VpnState.Disconnected;
    private bool _disposed;

    public WireGuardVpnService(string? executablePath = null)
    {
        _executable = executablePath ?? WireGuardLocator.FindExecutable();

        // Started on demand: a stopped timer costs nothing while no tunnel is up.
        _watchdog = new Timer(_ => _ = WatchdogTickAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public VpnKind Kind => VpnKind.WireGuard;

    public VpnState State => _state;

    public event EventHandler<VpnState>? StateChanged;

    public bool IsAvailable => _executable is not null;

    public string? UnavailableReason => IsAvailable
        ? null
        : $"WireGuard for Windows is not installed. Get it from {WireGuardLocator.DownloadUrl} and restart WireTV.";

    /// <summary>Path to the wireguard.exe being driven, for diagnostics.</summary>
    public string? ExecutablePath => _executable;

    public async Task ConnectAsync(VpnProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (_executable is null)
        {
            SetState(new VpnState(VpnStatus.Faulted, profile.Name, UnavailableReason));
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.ConfigPath) || !File.Exists(profile.ConfigPath))
        {
            SetState(new VpnState(VpnStatus.Faulted, profile.Name,
                $"The configuration file is missing: {profile.ConfigPath}"));
            return;
        }

        var tunnel = Path.GetFileNameWithoutExtension(profile.ConfigPath);

        if (!WireGuardLocator.IsValidTunnelName(tunnel))
        {
            SetState(new VpnState(VpnStatus.Faulted, profile.Name,
                $"'{tunnel}' is not a valid WireGuard tunnel name. Re-import the profile."));
            return;
        }

        var serviceName = ServicePrefix + tunnel;

        SetState(new VpnState(VpnStatus.Connecting, profile.Name, "Requesting administrator rights..."));

        try
        {
            // /installtunnelservice fails if a service for this tunnel already exists,
            // so a leftover from a previous run is removed first.
            if (ServiceExists(serviceName))
            {
                await RunWireGuardAsync($"/uninstalltunnelservice {tunnel}", ct).ConfigureAwait(false);
                await WaitForServiceGoneAsync(serviceName, StopTimeout, ct).ConfigureAwait(false);
            }

            SetState(new VpnState(VpnStatus.Connecting, profile.Name, "Starting tunnel..."));

            var result = await RunWireGuardAsync(
                $"/installtunnelservice \"{profile.ConfigPath}\"", ct).ConfigureAwait(false);

            if (result.WasCancelledByUser)
            {
                SetState(new VpnState(VpnStatus.Faulted, profile.Name,
                    "Connection cancelled: administrator rights were not granted."));
                return;
            }

            // The exit code alone is not proof: the command returns as soon as the
            // service is registered, so the service state is what actually decides.
            var running = await WaitForServiceRunningAsync(serviceName, StartTimeout, ct).ConfigureAwait(false);

            if (!running)
            {
                var reason = result.Succeeded
                    ? "The tunnel service did not start within the timeout. Check the config file and the WireGuard log."
                    : result.Describe();

                SetState(new VpnState(VpnStatus.Faulted, profile.Name, reason));
                return;
            }

            _tunnelName = tunnel;

            // Note: a running service means the tunnel is configured and the adapter is up.
            // It does not yet prove a handshake with the peer succeeded - surfacing real
            // handshake state needs "wg show", which is a follow-up refinement.
            SetState(new VpnState(VpnStatus.Connected, profile.Name, $"Tunnel {tunnel} is up.", DateTimeOffset.Now));
            StartWatchdog();
        }
        catch (OperationCanceledException)
        {
            SetState(new VpnState(VpnStatus.Faulted, profile.Name, "The connection attempt was cancelled."));
        }
        catch (Exception ex)
        {
            SetState(new VpnState(VpnStatus.Faulted, profile.Name, ex.Message));
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        var tunnel = _tunnelName ?? FindRunningTunnel();

        if (tunnel is null || _executable is null)
        {
            StopWatchdog();
            _tunnelName = null;
            SetState(VpnState.Disconnected);
            return;
        }

        var serviceName = ServicePrefix + tunnel;
        var profileName = _state.ProfileName;

        StopWatchdog();
        SetState(new VpnState(VpnStatus.Disconnecting, profileName, "Stopping tunnel..."));

        try
        {
            var result = await RunWireGuardAsync($"/uninstalltunnelservice {tunnel}", ct).ConfigureAwait(false);

            if (result.WasCancelledByUser)
            {
                // The tunnel is still up; reflect reality rather than claiming success.
                SetState(new VpnState(VpnStatus.Connected, profileName,
                    "Still connected: administrator rights were not granted.", _state.ConnectedSince));
                StartWatchdog();
                return;
            }

            var gone = await WaitForServiceGoneAsync(serviceName, StopTimeout, ct).ConfigureAwait(false);

            if (!gone)
            {
                SetState(new VpnState(VpnStatus.Faulted, profileName,
                    "The tunnel service did not stop within the timeout."));
                return;
            }

            _tunnelName = null;
            SetState(VpnState.Disconnected);
        }
        catch (Exception ex)
        {
            SetState(new VpnState(VpnStatus.Faulted, profileName, ex.Message));
        }
    }

    public Task<VpnState> RefreshAsync(CancellationToken ct = default)
    {
        if (_executable is null)
        {
            SetState(new VpnState(VpnStatus.Faulted, null, UnavailableReason));
            return Task.FromResult(_state);
        }

        // Do not fight an operation that is still in flight.
        if (_state.IsBusy)
            return Task.FromResult(_state);

        var tunnel = _tunnelName ?? FindRunningTunnel();

        if (tunnel is not null && IsServiceRunning(ServicePrefix + tunnel))
        {
            _tunnelName = tunnel;
            StartWatchdog();

            SetState(_state.IsConnected
                ? _state
                : new VpnState(VpnStatus.Connected, _state.ProfileName ?? tunnel,
                    $"Tunnel {tunnel} is up.", DateTimeOffset.Now));
        }
        else
        {
            _tunnelName = null;
            StopWatchdog();

            if (_state.Status != VpnStatus.Faulted)
                SetState(VpnState.Disconnected);
        }

        return Task.FromResult(_state);
    }

    private Task<ElevatedResult> RunWireGuardAsync(string arguments, CancellationToken ct)
        => WindowsElevation.RunAsync(_executable!, arguments, ct);

    /// <summary>Name of any running WireGuardTunnel$ service, so an externally started tunnel is adopted.</summary>
    private static string? FindRunningTunnel()
    {
        try
        {
            foreach (var service in ServiceController.GetServices())
            {
                using (service)
                {
                    if (service.ServiceName.StartsWith(ServicePrefix, StringComparison.OrdinalIgnoreCase) &&
                        service.Status == ServiceControllerStatus.Running)
                    {
                        return service.ServiceName[ServicePrefix.Length..];
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Enumeration can fail on locked-down machines; treat as "nothing running".
        }

        return null;
    }

    private static bool ServiceExists(string serviceName) => TryReadStatus(serviceName) is not null;

    private static bool IsServiceRunning(string serviceName)
        => TryReadStatus(serviceName) is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;

    private static ServiceControllerStatus? TryReadStatus(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
            return controller.Status;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // ServiceController throws rather than returning null for an unknown service.
            return null;
        }
    }

    private static async Task<bool> WaitForServiceRunningAsync(string serviceName, TimeSpan timeout, CancellationToken ct)
        => await PollAsync(() => TryReadStatus(serviceName) == ServiceControllerStatus.Running, timeout, ct)
            .ConfigureAwait(false);

    private static async Task<bool> WaitForServiceGoneAsync(string serviceName, TimeSpan timeout, CancellationToken ct)
        => await PollAsync(() => TryReadStatus(serviceName) is null, timeout, ct).ConfigureAwait(false);

    private static async Task<bool> PollAsync(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        return condition();
    }

    private void StartWatchdog() => _watchdog.Change(WatchInterval, WatchInterval);

    private void StopWatchdog() => _watchdog.Change(Timeout.Infinite, Timeout.Infinite);

    /// <summary>Notices a tunnel taken down outside WireTv (WireGuard UI, reboot, crash).</summary>
    private Task WatchdogTickAsync()
    {
        if (_disposed || _tunnelName is null || _state.IsBusy)
            return Task.CompletedTask;

        if (!IsServiceRunning(ServicePrefix + _tunnelName))
        {
            _tunnelName = null;
            StopWatchdog();
            SetState(new VpnState(VpnStatus.Disconnected, null, "The tunnel was stopped outside WireTV."));
        }

        return Task.CompletedTask;
    }

    private void SetState(VpnState state)
    {
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _watchdog.Dispose();
    }
}
