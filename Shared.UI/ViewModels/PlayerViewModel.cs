using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;
using WireTv.Core.Models;

namespace WireTv.UI.ViewModels;

/// <summary>
/// Owns the LibVLC engine and the single MediaPlayer the video view binds to.
///
/// Threading: LibVLC raises its events on its own threads, so every handler
/// marshals onto the UI thread before touching observable state. Playback calls are
/// never issued directly from an event handler either - libvlc can deadlock when
/// re-entered from a callback thread - so retries always go through a delay first.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    /// <summary>How many times a dropped stream is retried before giving up.</summary>
    private const int MaxRetries = 5;

    private readonly LibVLC? _libVlc;
    private readonly MediaPlayer? _mediaPlayer;
    private readonly string? _initError;

    private Channel? _currentChannel;
    private CancellationTokenSource? _retryCts;
    private int _retryAttempt;
    private bool _disposed;

    public PlayerViewModel()
    {
        if (StartupDiagnostics.LibVlcFailure is { } failure)
        {
            _initError = $"The VLC engine could not be loaded: {failure.Message}";
            StatusText = _initError;
            return;
        }

        try
        {
            // network-caching trades a little latency for resilience, which is the
            // right trade for live IPTV over the public internet.
            _libVlc = new LibVLC(
                "--network-caching=1500",
                "--live-caching=1500",
                "--no-video-title-show",
                "--drop-late-frames",
                "--skip-frames");

            _mediaPlayer = new MediaPlayer(_libVlc) { EnableHardwareDecoding = true };
            _mediaPlayer.Volume = Volume;

            _mediaPlayer.Playing += OnPlaying;
            _mediaPlayer.Paused += OnPaused;
            _mediaPlayer.Stopped += OnStopped;
            _mediaPlayer.EncounteredError += OnEncounteredError;
            _mediaPlayer.EndReached += OnEndReached;
            _mediaPlayer.Buffering += OnBuffering;
        }
        catch (Exception ex)
        {
            _initError = $"The VLC engine could not be started: {ex.Message}";
            StatusText = _initError;
        }
    }

    /// <summary>Bound to the VideoView. Null when the engine failed to load.</summary>
    public MediaPlayer? MediaPlayer => _mediaPlayer;

    public bool IsEngineReady => _mediaPlayer is not null;

    public string? EngineError => _initError;

    [ObservableProperty]
    private string _channelName = "No channel selected";

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private bool _isBuffering;

    [ObservableProperty]
    private int _volume = 80;

    [ObservableProperty]
    private bool _isMuted;

    /// <summary>True while an automatic retry is pending, so the UI can say so.</summary>
    [ObservableProperty]
    private bool _isRetrying;

    partial void OnVolumeChanged(int value)
    {
        if (_mediaPlayer is not null)
            _mediaPlayer.Volume = Math.Clamp(value, 0, 100);
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (_mediaPlayer is not null)
            _mediaPlayer.Mute = value;
    }

    /// <summary>Starts a channel, cancelling any retry still pending for the previous one.</summary>
    public void Play(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (_mediaPlayer is null || _libVlc is null)
        {
            StatusText = _initError ?? "The player is unavailable.";
            return;
        }

        CancelRetries();

        _currentChannel = channel;
        _retryAttempt = 0;

        ChannelName = channel.Name;
        StatusText = "Connecting...";
        IsBuffering = true;

        StartMedia(channel);
    }

    private void StartMedia(Channel channel)
    {
        if (_mediaPlayer is null || _libVlc is null)
            return;

        try
        {
            using var media = new Media(_libVlc, new Uri(channel.StreamUrl));

            // Provider-specific options from #EXTVLCOPT (user agent, referrer, ...).
            foreach (var option in channel.PlayerOptions)
                media.AddOption(option);

            _mediaPlayer.Play(media);
        }
        catch (UriFormatException)
        {
            IsBuffering = false;
            StatusText = $"Invalid stream URL: {channel.StreamUrl}";
        }
        catch (Exception ex)
        {
            IsBuffering = false;
            StatusText = $"Playback failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        if (_mediaPlayer is null)
            return;

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
            return;
        }

        // Resuming a live stream that was stopped needs a fresh media object;
        // Play() on its own only resumes a paused one.
        if (_mediaPlayer.State is VLCState.Paused)
            _mediaPlayer.Play();
        else if (_currentChannel is not null)
            Play(_currentChannel);
    }

    [RelayCommand]
    private void Stop()
    {
        CancelRetries();
        _mediaPlayer?.Stop();

        IsPlaying = false;
        IsBuffering = false;
        StatusText = "Stopped";
    }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    private void VolumeUp() => Volume = Math.Min(100, Volume + 5);

    [RelayCommand]
    private void VolumeDown() => Volume = Math.Max(0, Volume - 5);

    /// <summary>Manual retry of the current channel, resetting the automatic attempt counter.</summary>
    [RelayCommand]
    private void Reload()
    {
        if (_currentChannel is not null)
            Play(_currentChannel);
    }

    private void OnPlaying(object? sender, EventArgs e) => OnUiThread(() =>
    {
        _retryAttempt = 0;
        IsPlaying = true;
        IsBuffering = false;
        IsRetrying = false;
        StatusText = "Playing";
    });

    private void OnPaused(object? sender, EventArgs e) => OnUiThread(() =>
    {
        IsPlaying = false;
        StatusText = "Paused";
    });

    private void OnStopped(object? sender, EventArgs e) => OnUiThread(() =>
    {
        IsPlaying = false;
        IsBuffering = false;
    });

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e) => OnUiThread(() =>
    {
        // Cache reaching 100% is immediately followed by Playing, so avoid
        // flashing a "buffering 100%" message.
        if (e.Cache >= 100f)
            return;

        IsBuffering = true;
        StatusText = $"Buffering {e.Cache:0}%";
    });

    private void OnEncounteredError(object? sender, EventArgs e)
        => ScheduleRetry("The stream could not be opened");

    /// <summary>For a live stream EndReached means the source dropped, so it is retried too.</summary>
    private void OnEndReached(object? sender, EventArgs e)
        => ScheduleRetry("The stream ended");

    /// <summary>
    /// Reconnects after a drop with linear backoff. Runs on a background task so the
    /// libvlc callback thread that reported the failure is released immediately.
    /// </summary>
    private void ScheduleRetry(string reason)
    {
        var channel = _currentChannel;

        if (channel is null || _disposed)
            return;

        if (_retryAttempt >= MaxRetries)
        {
            OnUiThread(() =>
            {
                IsPlaying = false;
                IsBuffering = false;
                IsRetrying = false;
                StatusText = $"{reason}. Gave up after {MaxRetries} attempts.";
            });

            return;
        }

        _retryAttempt++;
        var attempt = _retryAttempt;
        var delay = TimeSpan.FromSeconds(Math.Min(2 * attempt, 10));

        CancelRetries();
        var cts = new CancellationTokenSource();
        _retryCts = cts;

        OnUiThread(() =>
        {
            IsPlaying = false;
            IsRetrying = true;
            StatusText = $"{reason}. Retrying in {delay.TotalSeconds:0}s ({attempt}/{MaxRetries})...";
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (cts.IsCancellationRequested || _disposed)
                return;

            OnUiThread(() =>
            {
                if (_disposed || _currentChannel != channel)
                    return;

                IsBuffering = true;
                StatusText = $"Reconnecting ({attempt}/{MaxRetries})...";
                StartMedia(channel);
            });
        });
    }

    private void CancelRetries()
    {
        var cts = Interlocked.Exchange(ref _retryCts, null);

        if (cts is null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        CancelRetries();

        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Playing -= OnPlaying;
            _mediaPlayer.Paused -= OnPaused;
            _mediaPlayer.Stopped -= OnStopped;
            _mediaPlayer.EncounteredError -= OnEncounteredError;
            _mediaPlayer.EndReached -= OnEndReached;
            _mediaPlayer.Buffering -= OnBuffering;

            _mediaPlayer.Dispose();
        }

        _libVlc?.Dispose();
    }
}
