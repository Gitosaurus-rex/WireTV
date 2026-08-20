using Avalonia;
using WireTv.UI;

namespace WireTv.Windows.UI;

internal static class Program
{
    /// <summary>
    /// STAThread is required by Windows for OLE/drag-drop and native file dialogs.
    /// Nothing before AppMain may touch any Avalonia type.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            // Loads the native libvlc. Must happen before the first LibVLC instance
            // is created. A single-file build unpacks its embedded engine here and
            // returns an explicit path; a folder build returns null and lets
            // LibVLCSharp probe next to the exe.
            var engineDirectory = VlcRuntime.Prepare();

            if (engineDirectory is null)
                LibVLCSharp.Shared.Core.Initialize();
            else
                LibVLCSharp.Shared.Core.Initialize(engineDirectory);
        }
        catch (Exception ex)
        {
            // Without the VLC engine there is no player, but the rest of the app is
            // still usable, so this is reported rather than fatal.
            StartupDiagnostics.LibVlcFailure = ex;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            StartupDiagnostics.WriteCrashLog(ex);
            throw;
        }
    }

    /// <summary>Also used by the Avalonia XAML previewer, which requires this exact signature.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
