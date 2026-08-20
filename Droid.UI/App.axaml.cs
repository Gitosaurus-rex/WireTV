using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using WireTv.UI;
using WireTv.UI.Views;

namespace WireTv.Droid;

// Qualified: Android's implicit usings put Android.App.Application in scope too,
// and an unqualified "Application" is ambiguous between the two.
public class App : global::Avalonia.Application
{
    /// <summary>
    /// The live shell, so MainActivity can route remote key codes into it.
    /// Null until the app has started, and null again if startup failed.
    /// </summary>
    public static TvShellView? Shell { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            var view = BuildMainView();
            Shell = view as TvShellView;
            single.MainView = view;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Builds the shell, or a readable error screen if that fails.
    ///
    /// Letting an exception escape here kills the process and the user sees a black
    /// screen with no explanation, which is exactly the failure that is hardest to
    /// report. Showing the message on the TV means the problem can be read off the
    /// screen without a cable.
    /// </summary>
    private static Control BuildMainView()
    {
        try
        {
            try
            {
                // Loads the native libvlc shipped by VideoLAN.LibVLC.Android. Must
                // happen before the first LibVLC instance is created.
                LibVLCSharp.Shared.Core.Initialize();
            }
            catch (Exception ex)
            {
                // Not fatal: the shell reports it and the rest of the app still works.
                StartupDiagnostics.LibVlcFailure = ex;
                CrashLog.Write("Core.Initialize", ex);
            }

            // No VPN backends: Android tunnels go through the system VpnService API,
            // which is a separate implementation and not part of this build. Passing
            // none leaves the VPN controls hidden rather than half-working.
            AppServices.Initialize();

            return new TvShellView(new AndroidDialogService());
        }
        catch (Exception ex)
        {
            CrashLog.Write("BuildMainView", ex);
            return BuildErrorView(ex);
        }
    }

    private static Control BuildErrorView(Exception ex) => new Border
    {
        Background = Brushes.Black,
        Padding = new Thickness(48),
        Child = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "WireTV could not start",
                        FontSize = 32,
                        FontWeight = FontWeight.Bold,
                        Foreground = Brushes.White
                    },
                    new TextBlock
                    {
                        Text = ex.ToString(),
                        FontSize = 15,
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(Color.Parse("#9AA3B2"))
                    },
                    new TextBlock
                    {
                        Text = "A copy is in Android/data/com.wiretv.player/files/crash.log",
                        FontSize = 15,
                        Foreground = new SolidColorBrush(Color.Parse("#4F8CFF"))
                    }
                }
            }
        }
    };
}
