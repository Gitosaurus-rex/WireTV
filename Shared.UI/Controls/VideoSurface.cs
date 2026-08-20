using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace WireTv.UI.Controls;

/// <summary>
/// Renders video into the Avalonia scene instead of a native child window.
///
/// LibVLCSharp ships a VideoView built on NativeControlHost, which on Windows means
/// a child HWND. A child HWND always paints over whatever the parent draws, so no
/// Avalonia control can ever be composited on top of it - the classic airspace
/// problem. A TV interface is overlays on top of full-screen video, so that
/// approach cannot work here.
///
/// Instead libvlc is told to decode into buffers we own (its "vmem" output) and the
/// frames are drawn as a normal image. Overlays, animation and transparency then all
/// behave like any other Avalonia content. The same code path also works on Android,
/// which matters for the shared UI.
///
/// The trade-off is that frames travel through system memory rather than staying on
/// the GPU, so this costs more CPU than a native surface would.
/// </summary>
public sealed class VideoSurface : Control
{
    /// <summary>
    /// Frames larger than this are scaled down by libvlc before they reach us.
    /// Nothing on screen benefits from more, and the per-frame cost scales with area.
    /// </summary>
    private const uint MaxDecodeWidth = 1920;

    private const uint MaxDecodeHeight = 1080;

    public static readonly StyledProperty<MediaPlayer?> MediaPlayerProperty =
        AvaloniaProperty.Register<VideoSurface, MediaPlayer?>(nameof(MediaPlayer));

    // libvlc keeps raw pointers to these delegates, so they must be rooted for the
    // lifetime of the control or the GC will collect them and the process will crash.
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCallback;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCallback;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCallback;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCallback;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCallback;

    private readonly object _sync = new();

    /// <summary>Double buffered: libvlc fills one frame while the UI draws the other.</summary>
    private readonly WriteableBitmap?[] _buffers = new WriteableBitmap?[2];

    private ILockedFramebuffer? _locked;
    private int _writeIndex;
    private int _readIndex = -1;
    private int _invalidatePending;
    private bool _attached;

    public VideoSurface()
    {
        _formatCallback = OnFormat;
        _cleanupCallback = OnCleanup;
        _lockCallback = OnLock;
        _unlockCallback = OnUnlock;
        _displayCallback = OnDisplay;
    }

    public MediaPlayer? MediaPlayer
    {
        get => GetValue(MediaPlayerProperty);
        set => SetValue(MediaPlayerProperty, value);
    }

    /// <summary>True once a frame has been received, so the UI can hide its placeholder.</summary>
    public bool HasFrame
    {
        get
        {
            lock (_sync)
                return _readIndex >= 0;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MediaPlayerProperty)
            Attach(change.GetNewValue<MediaPlayer?>());
    }

    /// <summary>
    /// Registers the callbacks. Must happen before playback starts, which is why the
    /// player is handed over at startup rather than when the first channel is picked.
    /// </summary>
    private void Attach(MediaPlayer? player)
    {
        if (player is null || _attached)
            return;

        player.SetVideoFormatCallbacks(_formatCallback, _cleanupCallback);
        player.SetVideoCallbacks(_lockCallback, _unlockCallback, _displayCallback);
        _attached = true;
    }

    // ---------------------------------------------------------------------
    // libvlc callbacks. All of these run on decoder threads, never the UI thread.
    // ---------------------------------------------------------------------

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height,
        ref uint pitches, ref uint lines)
    {
        // BGRA in memory order, which is what Skia wants; libvlc calls it RV32.
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);

        var scaled = Fit(width, height);
        width = (uint)scaled.Width;
        height = (uint)scaled.Height;

        pitches = width * 4;
        lines = height;

        var size = new PixelSize((int)width, (int)height);

        lock (_sync)
        {
            DisposeBuffers();

            for (var i = 0; i < _buffers.Length; i++)
            {
                _buffers[i] = new WriteableBitmap(
                    size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }

            _writeIndex = 0;
            _readIndex = -1;
        }

        // One picture buffer; we manage our own double buffering above it.
        return 1;
    }

    /// <summary>Caps the decoded size while preserving the source aspect ratio.</summary>
    private static PixelSize Fit(uint width, uint height)
    {
        if (width == 0 || height == 0)
            return new PixelSize(1280, 720);

        var scale = Math.Min(
            Math.Min(1.0, MaxDecodeWidth / (double)width),
            MaxDecodeHeight / (double)height);

        if (scale >= 1.0)
            return new PixelSize((int)width, (int)height);

        // Even dimensions keep chroma conversion on its fast path.
        var w = Math.Max(2, (int)(width * scale) & ~1);
        var h = Math.Max(2, (int)(height * scale) & ~1);

        return new PixelSize(w, h);
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (_sync)
        {
            _locked?.Dispose();
            _locked = null;
            DisposeBuffers();
            _readIndex = -1;
        }

        Dispatcher.UIThread.Post(InvalidateVisual);
    }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        WriteableBitmap? buffer;

        // Only the reference lookup is guarded. Holding _sync across lock/unlock
        // would block the UI thread in Render for the whole decode of a frame.
        lock (_sync)
            buffer = _buffers[_writeIndex];

        if (buffer is null)
            return IntPtr.Zero;

        var locked = buffer.Lock();
        _locked = locked;
        Marshal.WriteIntPtr(planes, locked.Address);

        return IntPtr.Zero;
    }

    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
        => Interlocked.Exchange(ref _locked, null)?.Dispose();

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (_sync)
        {
            _readIndex = _writeIndex;
            _writeIndex = (_writeIndex + 1) % _buffers.Length;
        }

        // Coalesce: a decoder that outruns the UI thread must not pile up
        // thousands of queued invalidations.
        if (Interlocked.Exchange(ref _invalidatePending, 1) == 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _invalidatePending, 0);
                InvalidateVisual();
            }, DispatcherPriority.Render);
        }
    }

    private void DisposeBuffers()
    {
        for (var i = 0; i < _buffers.Length; i++)
        {
            _buffers[i]?.Dispose();
            _buffers[i] = null;
        }
    }

    // ---------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        WriteableBitmap? frame;

        lock (_sync)
            frame = _readIndex >= 0 ? _buffers[_readIndex] : null;

        if (frame is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var source = new Rect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height);
        context.DrawImage(frame, source, Letterbox(source.Size, Bounds.Size));
    }

    /// <summary>Largest centred rectangle with the source aspect ratio that fits the control.</summary>
    private static Rect Letterbox(Size source, Size available)
    {
        var scale = Math.Min(available.Width / source.Width, available.Height / source.Height);

        var width = source.Width * scale;
        var height = source.Height * scale;

        return new Rect((available.Width - width) / 2, (available.Height - height) / 2, width, height);
    }
}
