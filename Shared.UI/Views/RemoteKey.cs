namespace WireTv.UI.Views;

/// <summary>
/// A remote-control action, independent of how the platform delivered it.
///
/// Exists because the two heads feed input in completely different ways: the
/// desktop translates Avalonia key events, while Android intercepts raw key codes
/// in DispatchKeyEvent - Avalonia's routed key events start at the focused element,
/// and on a freshly launched Android activity nothing is focused, so those events
/// never reach the shell at all.
/// </summary>
public enum RemoteKey
{
    Up,
    Down,
    Left,
    Right,

    /// <summary>D-pad centre, Enter, or the select button.</summary>
    Ok,

    Back,

    ChannelUp,
    ChannelDown,

    Guide,
    Info,
    Settings,

    PlayPause,
    Stop,

    Mute,
    VolumeUp,
    VolumeDown,

    FullScreen
}
