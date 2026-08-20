using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WireTv.Core.Epg;
using WireTv.Core.Models;
using WireTv.Core.Storage;
using WireTv.UI.Services;

namespace WireTv.UI.ViewModels;

/// <summary>Which full-screen layer is currently over the video, if any.</summary>
public enum TvOverlay
{
    None,
    Channels,
    Guide,
    Settings
}

/// <summary>
/// Which column of the channel drawer the remote is driving.
///
/// Ordered left to right, because Left and Right step through this enum. The menu
/// column exists because a TV remote has only a D-pad, OK and Back - there is no
/// Guide or Menu button to bind, so everything has to be reachable by walking left.
/// </summary>
public enum DrawerPane
{
    Menu,
    Groups,
    Channels
}

/// <summary>An entry in the drawer's left-hand menu column.</summary>
public enum DrawerAction
{
    Guide,
    Settings
}

/// <summary>Menu entry with its label, so the list can bind directly.</summary>
public sealed class DrawerMenuEntry
{
    public required DrawerAction Action { get; init; }

    public required string Label { get; init; }

    public override string ToString() => Label;
}

/// <summary>
/// Drives the TV shell: the provider list, the channel drawer, the guide and the
/// selection that feeds <see cref="PlayerViewModel"/>.
/// </summary>
public sealed partial class TvShellViewModel : ObservableObject, IDisposable
{
    /// <summary>How often the now/next line and the clock are recomputed.</summary>
    private static readonly TimeSpan GuideTickInterval = TimeSpan.FromSeconds(10);

    /// <summary>How long the info bar stays up after a channel change.</summary>
    private static readonly TimeSpan InfoBarDuration = TimeSpan.FromSeconds(6);

    private readonly PlaylistSourceStore _store;
    private readonly IDialogService _dialogs;
    private readonly DispatcherTimer _guideTimer;
    private readonly DispatcherTimer _infoBarTimer;

    private PlaylistSourceDocument _document = new();
    private IReadOnlyList<ChannelListItemViewModel> _allChannels = Array.Empty<ChannelListItemViewModel>();

    /// <summary>Cancels an EPG download still running when the user switches provider.</summary>
    private CancellationTokenSource? _epgCts;

    /// <summary>Guards against reacting to selection changes made by the loading code itself.</summary>
    private bool _suppressSourceReload;

    public TvShellViewModel(PlayerViewModel player, VpnViewModel vpn, IDialogService dialogs)
    {
        Player = player;
        Vpn = vpn;
        _dialogs = dialogs;
        _store = AppServices.SourceStore;

        // Always running: it drives the clock as well as the guide lines.
        _guideTimer = new DispatcherTimer { Interval = GuideTickInterval };
        _guideTimer.Tick += (_, _) => RefreshGuideState();
        _guideTimer.Start();

        _infoBarTimer = new DispatcherTimer { Interval = InfoBarDuration };
        _infoBarTimer.Tick += (_, _) =>
        {
            _infoBarTimer.Stop();
            IsInfoBarVisible = false;
        };

        UpdateClock();
    }

    // ------------------------------------------------------------------
    // TV shell state
    // ------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChannelDrawerOpen), nameof(IsGuideOpen), nameof(IsSettingsOpen),
        nameof(IsOverlayOpen), nameof(IsChromeVisible))]
    private TvOverlay _activeOverlay = TvOverlay.None;

    /// <summary>Bottom bar showing what is playing. Auto-hides so the video is unobstructed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChromeVisible))]
    private bool _isInfoBarVisible;

    [ObservableProperty]
    private string _clockText = string.Empty;

    /// <summary>
    /// Where the D-pad is inside the channel drawer. Kept separate from
    /// <see cref="SelectedChannel"/> so that arrowing through the list previews
    /// channels without zapping to each one in turn - pressing OK commits.
    /// </summary>
    [ObservableProperty]
    private ChannelListItemViewModel? _highlightedChannel;

    /// <summary>
    /// Which drawer column the remote drives. Tracked explicitly rather than read
    /// from Avalonia's focus, because the shell moves selection itself instead of
    /// relying on focus-based list navigation - see RemoteKey for why.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMenuPaneActive), nameof(IsGroupPaneActive), nameof(IsChannelPaneActive))]
    private DrawerPane _drawerPane = DrawerPane.Channels;

    public bool IsMenuPaneActive => DrawerPane == DrawerPane.Menu;

    public bool IsGroupPaneActive => DrawerPane == DrawerPane.Groups;

    public bool IsChannelPaneActive => DrawerPane == DrawerPane.Channels;

    /// <summary>The drawer's left-hand column: everything a dedicated button would have opened.</summary>
    public IReadOnlyList<DrawerMenuEntry> MenuEntries { get; } =
    [
        new DrawerMenuEntry { Action = DrawerAction.Guide, Label = "TV guide" },
        new DrawerMenuEntry { Action = DrawerAction.Settings, Label = "Settings" }
    ];

    [ObservableProperty]
    private DrawerMenuEntry? _selectedMenuEntry;

    /// <summary>Moves the highlight within the active drawer column.</summary>
    public void MoveDrawer(int delta)
    {
        switch (DrawerPane)
        {
            case DrawerPane.Menu:
                StepMenu(delta);
                break;

            case DrawerPane.Groups:
                StepGroup(delta);
                break;

            default:
                StepHighlight(delta);
                break;
        }
    }

    /// <summary>Steps one column left or right, clamped at the ends.</summary>
    public void StepDrawerPane(int delta)
    {
        var next = (int)DrawerPane + delta;
        DrawerPane = (DrawerPane)Math.Clamp(next, (int)DrawerPane.Menu, (int)DrawerPane.Channels);

        if (DrawerPane == DrawerPane.Menu)
            SelectedMenuEntry ??= MenuEntries[0];
    }

    /// <summary>
    /// What OK does depends on the column: the menu opens a layer, groups steps
    /// right into the channels it just filtered, and channels commits.
    /// </summary>
    public void ActivateDrawer()
    {
        switch (DrawerPane)
        {
            case DrawerPane.Menu:
                RunMenuEntry();
                break;

            case DrawerPane.Groups:
                DrawerPane = DrawerPane.Channels;
                break;

            default:
                PlayHighlightedCommand.Execute(null);
                break;
        }
    }

    private void RunMenuEntry()
    {
        switch (SelectedMenuEntry?.Action)
        {
            case DrawerAction.Guide:
                ShowGuideCommand.Execute(null);
                break;

            case DrawerAction.Settings:
                ShowSettingsCommand.Execute(null);
                break;
        }
    }

    private void StepMenu(int delta)
    {
        var index = SelectedMenuEntry is null ? 0 : MenuEntries.ToList().IndexOf(SelectedMenuEntry);
        SelectedMenuEntry = MenuEntries[Math.Clamp(index + delta, 0, MenuEntries.Count - 1)];
    }

    private void StepGroup(int delta)
    {
        if (Groups.Count == 0)
            return;

        var index = Groups.ToList().IndexOf(SelectedGroup);
        SelectedGroup = Groups[Math.Clamp((index < 0 ? 0 : index) + delta, 0, Groups.Count - 1)];

        // Changing group refilters the channel list, so the old highlight is gone.
        HighlightedChannel = Channels.FirstOrDefault();
    }

    private void StepHighlight(int delta)
    {
        if (Channels.Count == 0)
            return;

        var index = HighlightedChannel is null ? -1 : IndexOf(Channels, HighlightedChannel);
        var next = index < 0 ? 0 : Math.Clamp(index + delta, 0, Channels.Count - 1);

        HighlightedChannel = Channels[next];
    }

    public bool IsChannelDrawerOpen => ActiveOverlay == TvOverlay.Channels;

    public bool IsGuideOpen => ActiveOverlay == TvOverlay.Guide;

    public bool IsSettingsOpen => ActiveOverlay == TvOverlay.Settings;

    public bool IsOverlayOpen => ActiveOverlay != TvOverlay.None;

    /// <summary>Clock and VPN badge only appear alongside other chrome, never over bare video.</summary>
    public bool IsChromeVisible => IsOverlayOpen || IsInfoBarVisible;

    /// <summary>Built when the guide overlay opens, torn down when it closes.</summary>
    [ObservableProperty]
    private GuideViewModel? _guideView;

    /// <summary>Built when the settings overlay opens, torn down when it closes.</summary>
    [ObservableProperty]
    private SettingsViewModel? _settingsView;

    [RelayCommand]
    private void ToggleChannelDrawer()
    {
        if (ActiveOverlay == TvOverlay.Channels)
        {
            CloseOverlay();
            return;
        }

        HighlightedChannel = SelectedChannel ?? Channels.FirstOrDefault();
        DrawerPane = DrawerPane.Channels;
        ActiveOverlay = TvOverlay.Channels;
    }

    [RelayCommand]
    private void ShowGuide()
    {
        if (ActiveOverlay == TvOverlay.Guide)
        {
            CloseOverlay();
            return;
        }

        GuideView?.Dispose();
        GuideView = new GuideViewModel(Guide, _allChannels, PlayChannel, SelectedChannel);
        ActiveOverlay = TvOverlay.Guide;
    }

    /// <summary>
    /// Opens settings as a layer rather than a separate window, so the same screen
    /// works with a remote on a TV and with a mouse on the desktop.
    /// </summary>
    [RelayCommand]
    private async Task ShowSettingsAsync()
    {
        if (ActiveOverlay == TvOverlay.Settings)
        {
            await CloseOverlayAsync();
            return;
        }

        var settings = new SettingsViewModel(Vpn, _dialogs);
        await settings.LoadAsync();

        SettingsView = settings;
        ActiveOverlay = TvOverlay.Settings;
    }

    [RelayCommand]
    private async Task CloseOverlayAsync()
    {
        switch (ActiveOverlay)
        {
            case TvOverlay.Guide:
                GuideView?.Dispose();
                GuideView = null;
                break;

            case TvOverlay.Settings:
                SettingsView = null;

                // Playlists may have been added, edited or removed while it was open.
                await ReloadSourcesAsync();
                break;
        }

        ActiveOverlay = TvOverlay.None;
    }

    /// <summary>Fire-and-forget close for callers that cannot await, such as key handling.</summary>
    private void CloseOverlay() => _ = CloseOverlayAsync();

    /// <summary>Commits the highlighted channel. Bound to OK/Enter in the drawer.</summary>
    [RelayCommand]
    private void PlayHighlighted()
    {
        if (HighlightedChannel is not { } channel)
            return;

        SelectedChannel = channel;
        CloseOverlay();
    }

    /// <summary>Brings up the info bar and restarts its auto-hide countdown.</summary>
    [RelayCommand]
    private void ShowInfoBar()
    {
        IsInfoBarVisible = true;
        _infoBarTimer.Stop();
        _infoBarTimer.Start();
    }

    private void UpdateClock() => ClockText = DateTime.Now.ToString("HH:mm");

    public PlayerViewModel Player { get; }

    public VpnViewModel Vpn { get; }

    /// <summary>Set by the view; opening a window is not the ViewModel's job.</summary>

    public ObservableCollection<PlaylistSource> Sources { get; } = new();

    /// <summary>The parsed guide for the current provider. Empty until one loads.</summary>
    public EpgGuide Guide { get; private set; } = EpgGuide.Empty;

    /// <summary>Every channel in the loaded playlist, before search/group filtering.</summary>
    public IReadOnlyList<ChannelListItemViewModel> AllChannels => _allChannels;

    [ObservableProperty]
    private PlaylistSource? _selectedSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChannels))]
    private IReadOnlyList<ChannelListItemViewModel> _channels = Array.Empty<ChannelListItemViewModel>();

    [ObservableProperty]
    private ChannelListItemViewModel? _selectedChannel;

    [ObservableProperty]
    private IReadOnlyList<string> _groups = [ChannelGroup.AllChannels];

    [ObservableProperty]
    private string _selectedGroup = ChannelGroup.AllChannels;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    /// <summary>Guide state for the status bar: loading, loaded, or why it is missing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGuide))]
    private string _epgStatus = "No guide";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    /// <summary>Hides the sidebar and top bar for distraction-free viewing.</summary>
    [ObservableProperty]
    private bool _isFullScreen;

    public bool IsIdle => !IsBusy;

    public bool HasSources => Sources.Count > 0;

    public bool HasChannels => Channels.Count > 0;

    public bool HasGuide => !Guide.IsEmpty;

    public int TotalChannelCount => _allChannels.Count;

    public async Task InitializeAsync()
    {
        _document = await _store.LoadAsync();

        Player.Volume = Math.Clamp(_document.Volume, 0, 100);

        await Vpn.LoadAsync();

        ReloadSourceList();

        if (Player.EngineError is { } engineError)
            StatusMessage = engineError;
        else if (!HasSources)
            StatusMessage = "No playlist yet. Open Settings to add an M3U URL or file.";
    }

    /// <summary>Re-reads the provider list from disk, e.g. after the settings window closed.</summary>
    public async Task ReloadSourcesAsync()
    {
        var previousId = SelectedSource?.Id;

        _document = await _store.LoadAsync();
        ReloadSourceList(previousId);
    }

    private void ReloadSourceList(string? preferredId = null)
    {
        _suppressSourceReload = true;

        try
        {
            Sources.Clear();
            foreach (var source in _document.Sources)
                Sources.Add(source);

            OnPropertyChanged(nameof(HasSources));

            var wanted = preferredId ?? _document.LastUsedSourceId;
            var target = Sources.FirstOrDefault(s => s.Id == wanted) ?? Sources.FirstOrDefault();

            var changed = !ReferenceEquals(target, SelectedSource);
            SelectedSource = target;

            // Only kick off a load when the selection actually moved; re-selecting the
            // same source after closing Settings should not re-download the playlist.
            if (changed && target is not null)
            {
                _suppressSourceReload = false;
                _ = LoadSourceAsync(target);
            }
        }
        finally
        {
            _suppressSourceReload = false;
        }
    }

    partial void OnSelectedSourceChanged(PlaylistSource? value)
    {
        if (_suppressSourceReload || value is null)
            return;

        _ = LoadSourceAsync(value);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedGroupChanged(string value) => ApplyFilter();

    partial void OnSelectedChannelChanged(ChannelListItemViewModel? value)
    {
        if (value is null)
            return;

        _document.LastChannelId = value.Channel.Id;
        Player.Play(value.Channel);

        // Same reflex as a set-top box: changing channel says what you landed on.
        ShowInfoBar();
    }

    private async Task LoadSourceAsync(PlaylistSource source)
    {
        IsBusy = true;
        StatusMessage = $"Loading {source.Name}...";

        // A guide belongs to one provider; drop the old one immediately.
        CancelGuideLoad();
        Guide = EpgGuide.Empty;
        EpgStatus = "No guide";
        OnPropertyChanged(nameof(HasGuide));

        try
        {
            var playlist = await AppServices.PlaylistLoader.LoadAsync(source);

            _allChannels = playlist.Channels.Select(c => new ChannelListItemViewModel(c)).ToArray();

            Groups = [ChannelGroup.AllChannels, .. playlist.GetGroups().Select(g => g.Name)];
            SelectedGroup = ChannelGroup.AllChannels;

            ApplyFilter();
            OnPropertyChanged(nameof(TotalChannelCount));

            source.LastRefreshedUtc = DateTimeOffset.UtcNow;
            source.LastChannelCount = playlist.Count;
            _document.LastUsedSourceId = source.Id;
            await _store.SaveAsync(_document);

            // Groups includes the "All channels" pseudo-entry, which is not a real group.
            var groupCount = Groups.Count - 1;
            var summary = $"{playlist.Count} channels in {groupCount} {(groupCount == 1 ? "group" : "groups")}";

            StatusMessage = playlist.Warnings.Count == 0
                ? summary + "."
                : $"{summary} ({playlist.Warnings.Count} lines skipped).";

            RestoreLastChannel();

            // The playlist is usable now; the guide arrives when it arrives.
            _ = LoadGuideAsync(source, playlist);
        }
        catch (Exception ex)
        {
            _allChannels = Array.Empty<ChannelListItemViewModel>();
            Channels = Array.Empty<ChannelListItemViewModel>();
            Groups = [ChannelGroup.AllChannels];

            StatusMessage = $"Could not load '{source.Name}': {ex.Message}";
            await _dialogs.ShowMessageAsync("Playlist error", StatusMessage);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Downloads and parses the XMLTV guide in the background. Deliberately never
    /// throws into the caller: a missing guide degrades the app, it does not break it.
    /// </summary>
    private async Task LoadGuideAsync(PlaylistSource source, Playlist playlist)
    {
        // An explicit URL on the profile wins; otherwise use whatever the playlist
        // advertised (url-tvg for M3U, the account's xmltv.php for Xtream).
        var epgUrl = !string.IsNullOrWhiteSpace(source.EpgUrl) ? source.EpgUrl : playlist.EpgUrl;

        if (string.IsNullOrWhiteSpace(epgUrl))
        {
            EpgStatus = "No guide URL";
            return;
        }

        var cts = new CancellationTokenSource();
        _epgCts = cts;

        EpgStatus = "Loading guide...";

        try
        {
            var guide = await AppServices.EpgLoader
                .LoadAsync(epgUrl, playlist.Channels, ct: cts.Token)
                .ConfigureAwait(true);

            if (cts.IsCancellationRequested)
                return;

            Guide = guide;

            var matches = EpgMatcher.Match(guide, playlist.Channels);

            foreach (var item in _allChannels)
                item.EpgChannelId = matches.GetValueOrDefault(item.Channel.Id);

            RefreshGuideState();

            EpgStatus = guide.IsEmpty
                ? "Guide empty"
                : $"Guide: {matches.Count}/{_allChannels.Count} channels";

            OnPropertyChanged(nameof(HasGuide));
        }
        catch (OperationCanceledException)
        {
            // Provider switched while downloading; the new load owns the state now.
        }
        catch (Exception ex)
        {
            EpgStatus = $"Guide failed: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_epgCts, cts))
                _epgCts = null;

            cts.Dispose();
        }
    }

    private void CancelGuideLoad()
    {
        // The tick timer keeps running: it also drives the clock, which must not
        // stop just because the guide was dropped.
        var cts = Interlocked.Exchange(ref _epgCts, null);
        cts?.Cancel();
    }

    private void RefreshGuideState()
    {
        UpdateClock();

        if (Guide.IsEmpty)
            return;

        var now = DateTimeOffset.Now;

        foreach (var item in _allChannels)
            item.RefreshGuide(Guide, now);
    }

    /// <summary>Re-selects the channel that was playing when the app last closed, if it still exists.</summary>
    private void RestoreLastChannel()
    {
        if (SelectedChannel is not null || _document.LastChannelId is not { } lastId)
            return;

        var channel = _allChannels.FirstOrDefault(c => c.Channel.Id == lastId);

        if (channel is not null && Channels.Contains(channel))
            SelectedChannel = channel;
    }

    private void ApplyFilter()
    {
        IEnumerable<ChannelListItemViewModel> query = _allChannels;

        if (!string.Equals(SelectedGroup, ChannelGroup.AllChannels, StringComparison.Ordinal))
            query = query.Where(c => string.Equals(c.Group, SelectedGroup, StringComparison.OrdinalIgnoreCase));

        var search = SearchText.Trim();

        if (search.Length > 0)
            query = query.Where(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase));

        // Replaced wholesale rather than mutated: one notification instead of thousands.
        Channels = query.ToArray();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedSource is { } source)
            await LoadSourceAsync(source);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void ToggleFullScreen() => IsFullScreen = !IsFullScreen;

    [RelayCommand]
    private void ExitFullScreen() => IsFullScreen = false;

    /// <summary>Plays a channel chosen somewhere other than the sidebar, such as the guide.</summary>
    public void PlayChannel(Channel channel)
    {
        var item = _allChannels.FirstOrDefault(c => c.Channel.Id == channel.Id);

        if (item is not null && Channels.Contains(item))
            SelectedChannel = item;
        else
            Player.Play(channel);
    }

    /// <summary>Steps through the filtered list, which is what the channel up/down keys act on.</summary>
    [RelayCommand]
    private void NextChannel() => StepChannel(1);

    [RelayCommand]
    private void PreviousChannel() => StepChannel(-1);

    private void StepChannel(int delta)
    {
        if (Channels.Count == 0)
            return;

        var index = SelectedChannel is null ? -1 : IndexOf(Channels, SelectedChannel);
        var next = index < 0 ? 0 : (index + delta + Channels.Count) % Channels.Count;

        SelectedChannel = Channels[next];
    }

    /// <summary>IReadOnlyList has no IndexOf and LINQ offers no equivalent.</summary>
    private static int IndexOf(IReadOnlyList<ChannelListItemViewModel> channels, ChannelListItemViewModel channel)
    {
        for (var i = 0; i < channels.Count; i++)
        {
            if (ReferenceEquals(channels[i], channel))
                return i;
        }

        return -1;
    }

    /// <summary>Persists volume and last-played state. Called when the window closes.</summary>
    public async Task SaveStateAsync()
    {
        _document.Volume = Player.Volume;
        await _store.SaveAsync(_document);
    }

    public void Dispose()
    {
        _guideTimer.Stop();
        _infoBarTimer.Stop();
        GuideView?.Dispose();
        CancelGuideLoad();
    }
}
