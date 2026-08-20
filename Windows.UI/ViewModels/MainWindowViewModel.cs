using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTv.Core.Epg;
using OpenTv.Core.Models;
using OpenTv.Core.Storage;
using OpenTv.Windows.UI.Services;

namespace OpenTv.Windows.UI.ViewModels;

/// <summary>Which full-screen layer is currently over the video, if any.</summary>
public enum TvOverlay
{
    None,
    Channels,
    Guide
}

/// <summary>
/// Drives the TV shell: the provider list, the channel drawer, the guide and the
/// selection that feeds <see cref="PlayerViewModel"/>.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
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

    public MainWindowViewModel(PlayerViewModel player, VpnViewModel vpn, IDialogService dialogs)
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
    [NotifyPropertyChangedFor(nameof(IsChannelDrawerOpen), nameof(IsGuideOpen), nameof(IsOverlayOpen))]
    private TvOverlay _activeOverlay = TvOverlay.None;

    /// <summary>Bottom bar showing what is playing. Auto-hides so the video is unobstructed.</summary>
    [ObservableProperty]
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

    public bool IsChannelDrawerOpen => ActiveOverlay == TvOverlay.Channels;

    public bool IsGuideOpen => ActiveOverlay == TvOverlay.Guide;

    public bool IsOverlayOpen => ActiveOverlay != TvOverlay.None;

    /// <summary>Built when the guide overlay opens, torn down when it closes.</summary>
    [ObservableProperty]
    private GuideViewModel? _guideView;

    [RelayCommand]
    private void ToggleChannelDrawer()
    {
        if (ActiveOverlay == TvOverlay.Channels)
        {
            CloseOverlay();
            return;
        }

        HighlightedChannel = SelectedChannel ?? Channels.FirstOrDefault();
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

    [RelayCommand]
    private void CloseOverlay()
    {
        if (ActiveOverlay == TvOverlay.Guide)
        {
            GuideView?.Dispose();
            GuideView = null;
        }

        ActiveOverlay = TvOverlay.None;
    }

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
    public Func<Task>? OpenSettingsRequested { get; set; }

    public Func<Task>? OpenGuideRequested { get; set; }

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
            _guideTimer.Start();

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
        _guideTimer.Stop();

        var cts = Interlocked.Exchange(ref _epgCts, null);
        cts?.Cancel();
    }

    private void RefreshGuideState()
    {
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
    private async Task OpenSettingsAsync()
    {
        if (OpenSettingsRequested is { } open)
            await open();
    }

    [RelayCommand]
    private async Task OpenGuideAsync()
    {
        if (OpenGuideRequested is { } open)
            await open();
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

    public void Dispose() => CancelGuideLoad();
}
