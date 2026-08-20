using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using OpenTv.Windows.UI.Services;
using OpenTv.Windows.UI.ViewModels;

namespace OpenTv.Windows.UI.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly PlayerViewModel _player;

    /// <summary>The open guide window, so a second click focuses it instead of opening another.</summary>
    private GuideWindow? _guideWindow;

    public MainWindow()
    {
        InitializeComponent();

        var dialogs = new DialogService();

        _player = new PlayerViewModel();

        var vpn = new VpnViewModel(AppServices.Vpn, AppServices.VpnProfiles, dialogs);

        _viewModel = new MainWindowViewModel(_player, vpn, dialogs)
        {
            // Opening a window is the view's job, so the ViewModel asks for it.
            OpenSettingsRequested = ShowSettingsAsync,
            OpenGuideRequested = ShowGuideAsync
        };

        DataContext = _viewModel;

        // IsFullScreen drives both the chrome visibility (via bindings) and the
        // actual window state, which only the view can change.
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.IsFullScreen))
                WindowState = _viewModel.IsFullScreen ? WindowState.FullScreen : WindowState.Normal;
        };

        Opened += OnOpened;
        Closing += OnClosing;
    }


    private async void OnOpened(object? sender, EventArgs e)
    {
        // NativeControlHost creates the child window during layout, which can still
        // be pending when Opened fires. Attaching the MediaPlayer before that, or
        // starting playback before the handle exists, makes libvlc fall back to its
        // own top-level output window - so both wait for layout to settle.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);

        Video.MediaPlayer = _player.MediaPlayer;

        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        await _viewModel.InitializeAsync();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        await _viewModel.SaveStateAsync();
        _viewModel.Dispose();
        _player.Dispose();
    }

    /// <summary>
    /// The guide is a modeless window: leaving it open next to the player is the
    /// point, so the user can browse while a channel keeps running.
    /// </summary>
    private Task ShowGuideAsync()
    {
        if (_guideWindow is { } existing)
        {
            existing.Activate();
            return Task.CompletedTask;
        }

        var guide = new GuideWindow(
            _viewModel.Guide,
            _viewModel.AllChannels,
            _viewModel.PlayChannel,
            _viewModel.SelectedChannel);

        _guideWindow = guide;
        guide.Closed += (_, _) => _guideWindow = null;
        guide.Show(this);

        return Task.CompletedTask;
    }

    private async Task ShowSettingsAsync()
    {
        var settings = new SettingsWindow(_viewModel.Vpn);
        await settings.ShowDialog(this);

        // Playlists may have been added, edited or removed while the dialog was open.
        await _viewModel.ReloadSourcesAsync();
    }

    private void OnVideoDoubleTapped(object? sender, TappedEventArgs e)
        => _viewModel.ToggleFullScreenCommand.Execute(null);
}
