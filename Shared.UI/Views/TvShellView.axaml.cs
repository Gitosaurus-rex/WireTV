using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using WireTv.UI.Services;
using WireTv.UI.ViewModels;

namespace WireTv.UI.Views;

/// <summary>
/// The TV shell: full-screen video with everything else layered over it.
///
/// A UserControl rather than a Window, because Android has no windows - the
/// platform head decides what hosts this (a Window on desktop, an Activity's
/// content on Android).
///
/// Navigation is driven through <see cref="HandleRemoteKey"/> and moves selection
/// explicitly rather than leaning on focus-based list navigation. Avalonia routes
/// key events starting from the focused element, and on Android nothing is focused
/// when the activity launches, so a focus-dependent design simply does nothing
/// there. Owning the navigation also gives the drawer its preview-then-commit
/// behaviour, which focus-driven selection cannot express.
/// </summary>
public partial class TvShellView : UserControl
{
    private readonly TvShellViewModel _viewModel;
    private readonly PlayerViewModel _player;

    private TopLevel? _keySource;

    public TvShellView() : this(new NullDialogService())
    {
    }

    public TvShellView(IDialogService dialogs)
    {
        InitializeComponent();

        _player = new PlayerViewModel();

        var vpn = new VpnViewModel(AppServices.Vpn, AppServices.VpnProfiles, dialogs);

        _viewModel = new TvShellViewModel(_player, vpn, dialogs);

        DataContext = _viewModel;

        Focusable = true;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += OnLoaded;
    }

    /// <summary>Raised on the full-screen key. Meaningless on Android, which is always full screen.</summary>
    public Action? FullScreenToggleRequested { get; set; }

    public TvShellViewModel ViewModel => _viewModel;

    /// <summary>
    /// Desktop input path. Android bypasses this entirely and calls
    /// <see cref="HandleRemoteKey"/> from DispatchKeyEvent.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _keySource = TopLevel.GetTopLevel(this);

        if (_keySource is not null)
            _keySource.KeyDown += OnKeyDown;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_keySource is not null)
        {
            _keySource.KeyDown -= OnKeyDown;
            _keySource = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        // The surface renders into the Avalonia scene, so unlike a native child
        // window there is no handle to wait for - but the player still has to be
        // attached before the first Play call registers its video callbacks.
        Video.MediaPlayer = _player.MediaPlayer;

        Focus();

        await _viewModel.InitializeAsync();
    }

    /// <summary>Persists state and releases the engine. Called by the head when it shuts down.</summary>
    public async Task ShutdownAsync()
    {
        await _viewModel.SaveStateAsync();

        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.Dispose();
        _player.Dispose();
    }

    /// <summary>Reloads providers, e.g. after the settings screen closed.</summary>
    public Task ReloadSourcesAsync() => _viewModel.ReloadSourcesAsync();

    // ------------------------------------------------------------------
    // Input
    // ------------------------------------------------------------------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Typing in a text box must win over the remote shortcuts below.
        if (e.Source is TextBox && _viewModel.ActiveOverlay == TvOverlay.Settings)
            return;

        if (Translate(e.Key) is { } remote)
            e.Handled = HandleRemoteKey(remote);
    }

    private static RemoteKey? Translate(Key key) => key switch
    {
        Key.Up => RemoteKey.Up,
        Key.Down => RemoteKey.Down,
        Key.Left => RemoteKey.Left,
        Key.Right => RemoteKey.Right,
        Key.Enter or Key.Space or Key.Select => RemoteKey.Ok,
        Key.Escape or Key.Back => RemoteKey.Back,
        Key.PageUp => RemoteKey.ChannelUp,
        Key.PageDown => RemoteKey.ChannelDown,
        Key.G => RemoteKey.Guide,
        Key.I => RemoteKey.Info,
        Key.S => RemoteKey.Settings,
        Key.M or Key.VolumeMute => RemoteKey.Mute,
        Key.Add or Key.OemPlus or Key.VolumeUp => RemoteKey.VolumeUp,
        Key.Subtract or Key.OemMinus or Key.VolumeDown => RemoteKey.VolumeDown,
        Key.F11 => RemoteKey.FullScreen,
        _ => null
    };

    /// <summary>
    /// The single entry point for remote input. Returns true when the key was
    /// consumed, which Android uses to decide whether to pass it on to the system.
    /// </summary>
    public bool HandleRemoteKey(RemoteKey key) => _viewModel.ActiveOverlay switch
    {
        TvOverlay.Channels => HandleDrawer(key),
        TvOverlay.Guide => HandleGuide(key),
        TvOverlay.Settings => HandleSettings(key),
        _ => HandlePlayer(key)
    };

    /// <summary>Nothing is open: the remote drives the channel and opens layers.</summary>
    private bool HandlePlayer(RemoteKey key)
    {
        switch (key)
        {
            case RemoteKey.Ok:
                _viewModel.ToggleChannelDrawerCommand.Execute(null);
                return true;

            case RemoteKey.Up or RemoteKey.ChannelUp:
                _viewModel.PreviousChannelCommand.Execute(null);
                return true;

            case RemoteKey.Down or RemoteKey.ChannelDown:
                _viewModel.NextChannelCommand.Execute(null);
                return true;

            case RemoteKey.Left or RemoteKey.Right or RemoteKey.Info:
                _viewModel.ShowInfoBarCommand.Execute(null);
                return true;

            case RemoteKey.Guide:
                _viewModel.ShowGuideCommand.Execute(null);
                return true;

            case RemoteKey.Settings:
                _viewModel.ShowSettingsCommand.Execute(null);
                return true;

            case RemoteKey.PlayPause:
                _player.TogglePlayPauseCommand.Execute(null);
                return true;

            case RemoteKey.Stop:
                _player.StopCommand.Execute(null);
                return true;

            case RemoteKey.Mute:
                _player.ToggleMuteCommand.Execute(null);
                return true;

            case RemoteKey.VolumeUp:
                _player.VolumeUpCommand.Execute(null);
                return true;

            case RemoteKey.VolumeDown:
                _player.VolumeDownCommand.Execute(null);
                return true;

            case RemoteKey.FullScreen:
                FullScreenToggleRequested?.Invoke();
                return true;

            // Back over bare video is left to the platform, so Android can exit.
            default:
                return false;
        }
    }

    /// <summary>
    /// Channel drawer. Left and Right walk the three columns - channels, groups,
    /// menu - which is how the guide and settings are reached at all on a remote
    /// with no dedicated buttons.
    /// </summary>
    private bool HandleDrawer(RemoteKey key)
    {
        switch (key)
        {
            case RemoteKey.Up:
                _viewModel.MoveDrawer(-1);
                ScrollDrawerIntoView();
                return true;

            case RemoteKey.Down:
                _viewModel.MoveDrawer(1);
                ScrollDrawerIntoView();
                return true;

            case RemoteKey.Left:
                _viewModel.StepDrawerPane(-1);
                ScrollDrawerIntoView();
                return true;

            case RemoteKey.Right:
                _viewModel.StepDrawerPane(1);
                ScrollDrawerIntoView();
                return true;

            case RemoteKey.Ok:
                _viewModel.ActivateDrawer();
                ScrollDrawerIntoView();
                return true;

            case RemoteKey.Back:
                _viewModel.CloseOverlayCommand.Execute(null);
                return true;

            case RemoteKey.Guide:
                _viewModel.ShowGuideCommand.Execute(null);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Guide: arrows move between and within the day, channel and schedule columns.</summary>
    private bool HandleGuide(RemoteKey key)
    {
        if (_viewModel.GuideView is not { } guide)
            return false;

        switch (key)
        {
            case RemoteKey.Up:
                guide.Move(-1);
                ScrollGuideIntoView();
                return true;

            case RemoteKey.Down:
                guide.Move(1);
                ScrollGuideIntoView();
                return true;

            case RemoteKey.Left:
                guide.StepPane(-1);
                ScrollGuideIntoView();
                return true;

            case RemoteKey.Right:
                guide.StepPane(1);
                ScrollGuideIntoView();
                return true;

            case RemoteKey.Ok:
                if (guide.Activate())
                    _viewModel.CloseOverlayCommand.Execute(null);
                else
                    ScrollGuideIntoView();

                return true;

            // Back returns to the drawer the guide was opened from, rather than all
            // the way to bare video, so one Back never loses the user's place.
            case RemoteKey.Back or RemoteKey.Guide:
                _viewModel.CloseOverlayCommand.Execute(null);
                _viewModel.ToggleChannelDrawerCommand.Execute(null);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Settings is the one layer that uses real focus, because text fields need it
    /// for the on-screen keyboard. Arrows therefore walk the focus chain instead of
    /// moving a selection.
    /// </summary>
    private bool HandleSettings(RemoteKey key)
    {
        switch (key)
        {
            // Back to the drawer, not to bare video: settings was opened from there.
            case RemoteKey.Back:
                _viewModel.CloseOverlayCommand.Execute(null);
                _viewModel.ToggleChannelDrawerCommand.Execute(null);
                return true;

            case RemoteKey.Up or RemoteKey.Left:
                return MoveFocus(NavigationDirection.Previous);

            case RemoteKey.Down or RemoteKey.Right:
                return MoveFocus(NavigationDirection.Next);

            case RemoteKey.Ok:
                return ActivateFocused();

            default:
                return false;
        }
    }

    private bool MoveFocus(NavigationDirection direction)
    {
        var current = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as InputElement;

        var next = KeyboardNavigationHandler.GetNext(current ?? this, direction);
        next?.Focus(NavigationMethod.Directional);

        return next is not null;
    }

    /// <summary>
    /// OK on a focused control. Buttons run their command; a text box just keeps
    /// focus, which is what raises the on-screen keyboard on Android.
    /// </summary>
    private bool ActivateFocused()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

        switch (focused)
        {
            case Button { Command: { } command } button when command.CanExecute(button.CommandParameter):
                command.Execute(button.CommandParameter);
                return true;

            case ComboBox combo:
                combo.IsDropDownOpen = !combo.IsDropDownOpen;
                return true;

            default:
                return false;
        }
    }

    // ------------------------------------------------------------------
    // Keeping the highlighted row on screen
    // ------------------------------------------------------------------

    private void ScrollDrawerIntoView()
    {
        switch (_viewModel.DrawerPane)
        {
            case DrawerPane.Menu:
                ScrollTo(MenuList, _viewModel.SelectedMenuEntry);
                break;

            case DrawerPane.Groups:
                ScrollTo(GroupList, _viewModel.SelectedGroup);
                break;

            default:
                ScrollTo(ChannelList, _viewModel.HighlightedChannel);
                break;
        }
    }

    private void ScrollGuideIntoView()
    {
        if (_viewModel.GuideView is not { } guide)
            return;

        switch (guide.Pane)
        {
            case GuidePane.Days:
                ScrollTo(GuideDayList, guide.SelectedDay);
                break;

            case GuidePane.Channels:
                ScrollTo(GuideChannelList, guide.SelectedChannel);
                break;

            default:
                ScrollTo(GuideProgrammeList, guide.SelectedProgramme);
                break;
        }
    }

    private static void ScrollTo(ListBox list, object? item)
    {
        if (item is null)
            return;

        // Posted: the container for a newly selected item may not exist until the
        // next layout pass, and ScrollIntoView silently does nothing without one.
        Dispatcher.UIThread.Post(() => list.ScrollIntoView(item), DispatcherPriority.Background);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TvShellViewModel.ActiveOverlay))
            return;

        switch (_viewModel.ActiveOverlay)
        {
            case TvOverlay.Channels:
                ScrollDrawerIntoView();
                break;

            case TvOverlay.Guide:
                ScrollGuideIntoView();
                break;

            case TvOverlay.Settings:
                // Settings is focus-driven, so put focus on its first control.
                Dispatcher.UIThread.Post(
                    () => MoveFocus(NavigationDirection.Next), DispatcherPriority.Loaded);
                break;

            default:
                Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Background);
                break;
        }
    }
}
