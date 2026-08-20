using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using WireTv.UI;
using WireTv.Windows.UI.Services;
using WireTv.Windows.UI.Views;
using WireTv.Windows.Vpn;

namespace WireTv.Windows.UI;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The only place the Windows-specific pieces are named. Everything the
            // shared UI touches goes through AppServices from here on.
            AppServices.Initialize(
                protector: new DpapiSecretProtector(),
                vpnBackends: [new WireGuardVpnService()],
                vpnProfileImporter: new WindowsVpnProfileImporter());

            desktop.MainWindow = new TvShell();

            // Release LibVLC and the VPN watchdog deterministically. Note this does
            // not tear down an active tunnel - see WireGuardVpnService for why.
            desktop.ShutdownRequested += (_, _) => AppServices.Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
