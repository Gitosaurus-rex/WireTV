using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using Android.Views;
using WireTv.UI.Views;

namespace WireTv.Droid;

/// <summary>
/// The single activity that hosts the whole app.
///
/// Two launcher categories are declared: LEANBACK_LAUNCHER puts WireTV on the
/// Android TV home screen, and LAUNCHER keeps it visible on phones and tablets
/// so the same APK is installable everywhere.
/// </summary>
[Activity(
    // Stable java name instead of the generated CRC one, so logs and adb commands
    // are readable.
    Name = "com.wiretv.player.MainActivity",
    Label = "WireTV",
    Icon = "@mipmap/appicon",
    Banner = "@drawable/banner",
    Theme = "@style/WireTvTheme.Splash",
    MainLauncher = true,
    // Required from Android 12 onwards for any component with an intent filter.
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ScreenOrientation = ScreenOrientation.Landscape,
    // Declared so the activity is not recreated on these changes, which would
    // restart playback every time the TV reports a UI-mode or size change.
    ConfigurationChanges =
        ConfigChanges.Orientation |
        ConfigChanges.ScreenSize |
        ConfigChanges.ScreenLayout |
        ConfigChanges.SmallestScreenSize |
        ConfigChanges.UiMode |
        ConfigChanges.KeyboardHidden |
        ConfigChanges.Keyboard)]
[IntentFilter(
    [Intent.ActionMain],
    Categories = [Intent.CategoryLeanbackLauncher, Intent.CategoryLauncher])]
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        // Installed before anything else can throw. A startup crash on a TV shows
        // nothing but a black screen, so the reason has to reach a file the user can
        // actually open.
        //
        // Guarded because a logger that throws would itself become the crash, and an
        // unexplained black screen is exactly what this is here to prevent.
        try
        {
            CrashLog.Install(this);
        }
        catch
        {
            // Continue without crash logging rather than failing to start.
        }

        base.OnCreate(savedInstanceState);
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder).WithInterFont();

    /// <summary>
    /// Remote input is taken here rather than through Avalonia's key events.
    ///
    /// Avalonia routes key events starting at the focused element, and a freshly
    /// launched activity has nothing focused, so a handler further up never runs -
    /// which is exactly why the D-pad did nothing at all. Intercepting at the
    /// activity is also how Android TV apps are expected to read the remote.
    /// </summary>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e?.Action == KeyEventActions.Down &&
            App.Shell is { } shell &&
            Map(e.KeyCode) is { } remote &&
            shell.HandleRemoteKey(remote))
        {
            return true;
        }

        return base.DispatchKeyEvent(e);
    }

    /// <summary>
    /// Volume keys are deliberately absent: on a TV they belong to the television or
    /// the amplifier, and swallowing them would leave the user unable to change the
    /// volume at all.
    /// </summary>
    private static RemoteKey? Map(Keycode code) => code switch
    {
        Keycode.DpadUp => RemoteKey.Up,
        Keycode.DpadDown => RemoteKey.Down,
        Keycode.DpadLeft => RemoteKey.Left,
        Keycode.DpadRight => RemoteKey.Right,

        Keycode.DpadCenter or Keycode.Enter or Keycode.NumpadEnter or Keycode.ButtonA
            => RemoteKey.Ok,

        Keycode.Back or Keycode.ButtonB => RemoteKey.Back,

        Keycode.ChannelUp => RemoteKey.ChannelUp,
        Keycode.ChannelDown => RemoteKey.ChannelDown,

        Keycode.Guide or Keycode.TvContentsMenu or Keycode.ProgRed => RemoteKey.Guide,
        Keycode.Info or Keycode.ProgBlue => RemoteKey.Info,
        Keycode.Menu or Keycode.Settings => RemoteKey.Settings,

        Keycode.MediaPlayPause or Keycode.MediaPlay or Keycode.MediaPause => RemoteKey.PlayPause,
        Keycode.MediaStop => RemoteKey.Stop,

        _ => null
    };
}

/// <summary>
/// Writes unhandled exceptions to the app's external files directory, which a file
/// manager can read without adb or root:
/// /sdcard/Android/data/com.wiretv.player/files/crash.log
/// </summary>
internal static class CrashLog
{
    private static string? _path;

    public static void Install(Context context)
    {
        // GetExternalFilesDir is app-scoped, so no storage permission is needed, but
        // unlike the private files dir it is visible over USB and to file managers.
        _path = Path.Combine(
            context.GetExternalFilesDir(null)?.AbsolutePath ?? context.FilesDir!.AbsolutePath,
            "crash.log");

        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            Write("AndroidEnvironment", e.Exception);
            e.Handled = false;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain", e.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, e) =>
            Write("Task", e.Exception);
    }

    public static void Write(string source, Exception? ex)
    {
        if (_path is null || ex is null)
            return;

        // Local rather than System.Environment.NewLine inline: "Environment" is
        // ambiguous here because Android.OS.Environment is also in scope.
        var newLine = "\n";

        try
        {
            File.AppendAllText(_path, $"[{DateTimeOffset.Now:O}] {source}{newLine}{ex}{newLine}{newLine}");
        }
        catch
        {
            // Nothing useful to do if even logging fails.
        }
    }
}
