using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using WireTv.UI.Views;
using WireTv.Windows.UI.Services;

namespace WireTv.Windows.UI.Views;

/// <summary>
/// Desktop frame around the shared <see cref="TvShellView"/>.
///
/// Everything the user sees lives in the shared control, settings included. This
/// class only supplies what is genuinely desktop-specific: a window, the Windows
/// dialog implementation, and full-screen toggling.
/// </summary>
public partial class TvShell : Window
{
    private readonly TvShellView _shell;

    public TvShell()
    {
        InitializeComponent();

        _shell = new TvShellView(new DialogService())
        {
            FullScreenToggleRequested = ToggleFullScreen
        };

        Content = _shell;

        Closing += OnClosing;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
        => await _shell.ShutdownAsync();

    private void ToggleFullScreen()
        => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
}
