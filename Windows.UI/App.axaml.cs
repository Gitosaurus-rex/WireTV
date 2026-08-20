using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpenTv.Windows.UI.Views;

namespace OpenTv.Windows.UI;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();

            // Release LibVLC and the VPN watchdog deterministically. Note this does
            // not tear down an active tunnel - see WireGuardVpnService for why.
            desktop.ShutdownRequested += (_, _) => AppServices.Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
