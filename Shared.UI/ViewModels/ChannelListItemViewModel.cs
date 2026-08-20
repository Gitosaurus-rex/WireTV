using CommunityToolkit.Mvvm.ComponentModel;
using WireTv.Core.Epg;
using WireTv.Core.Models;

namespace WireTv.UI.ViewModels;

/// <summary>
/// A playlist channel plus the guide data currently shown next to it.
///
/// <see cref="Channel"/> is immutable and platform-neutral by design, so the
/// changing "what is on now" state lives here in the UI layer instead.
/// </summary>
public sealed partial class ChannelListItemViewModel : ObservableObject
{
    public ChannelListItemViewModel(Channel channel)
    {
        Channel = channel;
    }

    public Channel Channel { get; }

    public string Name => Channel.Name;

    public string Group => Channel.Group;

    public int? Number => Channel.Number;

    /// <summary>Guide channel this was matched to, or null when the guide has nothing for it.</summary>
    public string? EpgChannelId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNow))]
    private string? _nowTitle;

    [ObservableProperty]
    private string? _nowTimeRange;

    /// <summary>How far the current programme has run, as a percentage for the progress bar.</summary>
    [ObservableProperty]
    private double _nowProgress;

    [ObservableProperty]
    private string? _nextTitle;

    public bool HasNow => !string.IsNullOrEmpty(NowTitle);

    /// <summary>Refreshes the now/next line. Called on load and on the guide tick.</summary>
    public void RefreshGuide(EpgGuide guide, DateTimeOffset moment)
    {
        if (EpgChannelId is null)
        {
            Clear();
            return;
        }

        var airing = guide.GetAiring(EpgChannelId, moment);

        if (airing is null)
        {
            Clear();
        }
        else
        {
            NowTitle = airing.Title;
            NowTimeRange = $"{airing.Start.LocalDateTime:HH:mm} - {airing.Stop.LocalDateTime:HH:mm}";
            NowProgress = airing.ProgressAt(moment) * 100;
        }

        NextTitle = guide.GetNext(EpgChannelId, moment)?.Title;
    }

    private void Clear()
    {
        NowTitle = null;
        NowTimeRange = null;
        NowProgress = 0;
    }

    public override string ToString() => Name;
}
