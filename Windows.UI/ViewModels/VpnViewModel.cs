using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTv.Core.Vpn;
using OpenTv.Windows.UI.Services;
using OpenTv.Windows.Vpn;

namespace OpenTv.Windows.UI.ViewModels;

/// <summary>
/// Presents the VPN state and profile list. Shared by the main window's status
/// badge and the settings window's VPN tab, so both always show the same truth.
/// </summary>
public sealed partial class VpnViewModel : ObservableObject
{
    private readonly VpnManager _vpn;
    private readonly VpnProfileStore _store;
    private readonly IDialogService _dialogs;

    private VpnProfileDocument _document = new();

    public VpnViewModel(VpnManager vpn, VpnProfileStore store, IDialogService dialogs)
    {
        _vpn = vpn;
        _store = store;
        _dialogs = dialogs;

        _vpn.StateChanged += OnVpnStateChanged;
    }

    public ObservableCollection<VpnProfile> Profiles { get; } = new();

    [ObservableProperty]
    private VpnProfile? _selectedProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    private VpnStatus _status = VpnStatus.Disconnected;

    [ObservableProperty]
    private string? _statusDetail;

    [ObservableProperty]
    private string? _activeProfileName;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Set when WireGuard for Windows is not installed on this machine.</summary>
    [ObservableProperty]
    private string? _backendWarning;

    public bool IsConnected => Status == VpnStatus.Connected;

    public string StatusLabel => Status switch
    {
        VpnStatus.Connected => ActiveProfileName is null ? "VPN connected" : $"VPN: {ActiveProfileName}",
        VpnStatus.Connecting => "VPN connecting...",
        VpnStatus.Disconnecting => "VPN disconnecting...",
        VpnStatus.Faulted => "VPN error",
        _ => "VPN off"
    };

    public IBrush StatusBrush => Status switch
    {
        VpnStatus.Connected => new SolidColorBrush(Color.Parse("#35C759")),
        VpnStatus.Connecting or VpnStatus.Disconnecting => new SolidColorBrush(Color.Parse("#F0A44A")),
        VpnStatus.Faulted => new SolidColorBrush(Color.Parse("#FF5C5C")),
        _ => new SolidColorBrush(Color.Parse("#6B7280"))
    };

    public async Task LoadAsync()
    {
        _document = await _store.LoadAsync();

        Profiles.Clear();
        foreach (var profile in _document.Profiles)
            Profiles.Add(profile);

        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == _document.LastUsedProfileId) ?? Profiles.FirstOrDefault();

        var backend = _vpn.GetBackend(VpnKind.WireGuard);
        BackendWarning = backend is { IsAvailable: false } ? backend.UnavailableReason : null;

        // A tunnel can outlive the app, so adopt whatever is already running.
        await _vpn.RefreshAsync();
    }

    [RelayCommand]
    private async Task ImportProfileAsync()
    {
        var path = await _dialogs.PickFileAsync(
            "Select a WireGuard configuration",
            "WireGuard configuration",
            ["*.conf"]);

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var profile = WireGuardProfileImporter.Import(path, Profiles);

            _document.Profiles.Add(profile);
            Profiles.Add(profile);
            SelectedProfile = profile;

            await _store.SaveAsync(_document);
        }
        catch (VpnException ex)
        {
            await _dialogs.ShowMessageAsync("Import failed", ex.Message);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("Import failed", $"The file could not be imported: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RemoveProfileAsync()
    {
        if (SelectedProfile is not { } profile)
            return;

        var confirmed = await _dialogs.ConfirmAsync(
            "Remove profile",
            $"Remove '{profile.Name}' and delete its configuration file?");

        if (!confirmed)
            return;

        // Stop the tunnel first, or the service would keep running with no profile behind it.
        if (IsConnected && _vpn.ActiveProfile?.Id == profile.Id)
            await DisconnectAsync();

        WireGuardProfileImporter.DeleteConfig(profile);

        _document.Profiles.RemoveAll(p => p.Id == profile.Id);
        Profiles.Remove(profile);
        SelectedProfile = Profiles.FirstOrDefault();

        await _store.SaveAsync(_document);
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (SelectedProfile is not { } profile || IsBusy)
            return;

        IsBusy = true;

        try
        {
            await _vpn.ConnectAsync(profile);

            _document.LastUsedProfileId = profile.Id;
            await _store.SaveAsync(_document);
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("VPN", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (IsBusy)
            return;

        IsBusy = true;

        try
        {
            await _vpn.DisconnectAsync();
        }
        catch (Exception ex)
        {
            await _dialogs.ShowMessageAsync("VPN", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Connects or disconnects depending on the current state. Bound to the status badge.</summary>
    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (IsConnected)
            await DisconnectAsync();
        else
            await ConnectAsync();
    }

    private void OnVpnStateChanged(object? sender, VpnState state)
    {
        // Backends raise this from watchdog and process threads.
        Dispatcher.UIThread.Post(() =>
        {
            Status = state.Status;
            StatusDetail = state.Message;
            ActiveProfileName = state.ProfileName;

            // StatusLabel folds in the profile name, so it must be refreshed too.
            OnPropertyChanged(nameof(StatusLabel));
        });
    }
}
