using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTv.Core.Epg;
using OpenTv.Core.Models;

namespace OpenTv.Windows.UI.ViewModels;

/// <summary>One row in the schedule list.</summary>
public sealed partial class GuideProgrammeViewModel : ObservableObject
{
    public GuideProgrammeViewModel(EpgProgramme programme)
    {
        Programme = programme;
    }

    public EpgProgramme Programme { get; }

    public string Title => Programme.Title;

    public string TimeRange =>
        $"{Programme.Start.LocalDateTime:HH:mm} - {Programme.Stop.LocalDateTime:HH:mm}";

    public string? SubTitle => Programme.SubTitle;

    public string? Description => Programme.Description;

    /// <summary>Category and episode number folded into one caption line.</summary>
    public string? Meta
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(Programme.Category))
                parts.Add(Programme.Category!);

            if (!string.IsNullOrWhiteSpace(Programme.EpisodeNumber))
                parts.Add(Programme.EpisodeNumber!);

            parts.Add($"{Programme.Duration.TotalMinutes:0} min");

            return string.Join("  -  ", parts);
        }
    }

    [ObservableProperty]
    private bool _isAiringNow;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _hasEnded;

    public void RefreshState(DateTimeOffset moment)
    {
        IsAiringNow = Programme.IsAiringAt(moment);
        Progress = IsAiringNow ? Programme.ProgressAt(moment) * 100 : 0;
        HasEnded = Programme.Stop <= moment;
    }
}

/// <summary>A selectable day in the guide.</summary>
public sealed class GuideDayViewModel
{
    public GuideDayViewModel(DateTimeOffset date, string label)
    {
        Date = date;
        Label = label;
    }

    /// <summary>Local midnight starting this day.</summary>
    public DateTimeOffset Date { get; }

    public string Label { get; }

    public override string ToString() => Label;
}

/// <summary>
/// Drives the TV guide window: channels on the left, the selected channel's
/// schedule for the selected day on the right.
/// </summary>
public sealed partial class GuideViewModel : ObservableObject, IDisposable
{
    /// <summary>How often "on now" and the progress bars are recomputed.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly EpgGuide _guide;
    private readonly IReadOnlyList<ChannelListItemViewModel> _allChannels;
    private readonly Action<Channel> _play;
    private readonly DispatcherTimer _timer;

    public GuideViewModel(
        EpgGuide guide,
        IReadOnlyList<ChannelListItemViewModel> channels,
        Action<Channel> play,
        ChannelListItemViewModel? initialSelection = null)
    {
        _guide = guide;
        _allChannels = channels;
        _play = play;

        Days = BuildDays();
        SelectedDay = Days[0];

        ApplyFilter();

        SelectedChannel = initialSelection is not null && Channels.Contains(initialSelection)
            ? initialSelection
            : Channels.FirstOrDefault();

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public IReadOnlyList<GuideDayViewModel> Days { get; }

    public ObservableCollection<GuideProgrammeViewModel> Programmes { get; } = new();

    [ObservableProperty]
    private IReadOnlyList<ChannelListItemViewModel> _channels = Array.Empty<ChannelListItemViewModel>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ChannelListItemViewModel? _selectedChannel;

    [ObservableProperty]
    private GuideDayViewModel? _selectedDay;

    /// <summary>
    /// Set to whatever is on air after a schedule loads. The window watches this to
    /// scroll it into view - a guide that opens at 00:00 is close to useless.
    /// </summary>
    [ObservableProperty]
    private GuideProgrammeViewModel? _selectedProgramme;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public bool HasSelection => SelectedChannel is not null;

    /// <summary>Shown when the selected channel has no guide entries for the chosen day.</summary>
    public bool HasProgrammes => Programmes.Count > 0;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedChannelChanged(ChannelListItemViewModel? value) => LoadSchedule();

    partial void OnSelectedDayChanged(GuideDayViewModel? value) => LoadSchedule();

    /// <summary>Seven days starting today, which is as far as guides reliably reach.</summary>
    private static IReadOnlyList<GuideDayViewModel> BuildDays()
    {
        var today = DateTimeOffset.Now;
        var midnight = new DateTimeOffset(today.Year, today.Month, today.Day, 0, 0, 0, today.Offset);

        var days = new List<GuideDayViewModel>();

        for (var i = 0; i < 7; i++)
        {
            var date = midnight.AddDays(i);

            var label = i switch
            {
                0 => "Today",
                1 => "Tomorrow",
                _ => date.LocalDateTime.ToString("ddd d MMM")
            };

            days.Add(new GuideDayViewModel(date, label));
        }

        return days;
    }

    private void ApplyFilter()
    {
        var search = SearchText.Trim();

        Channels = search.Length == 0
            ? _allChannels
            : _allChannels.Where(c => c.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (SelectedChannel is not null && !Channels.Contains(SelectedChannel))
            SelectedChannel = Channels.FirstOrDefault();
    }

    private void LoadSchedule()
    {
        Programmes.Clear();

        if (SelectedChannel is not { EpgChannelId: { } epgId } || SelectedDay is not { } day)
        {
            StatusText = SelectedChannel is null
                ? "Select a channel."
                : $"No guide data matched '{SelectedChannel.Name}'.";

            OnPropertyChanged(nameof(HasProgrammes));
            return;
        }

        var now = DateTimeOffset.Now;

        foreach (var programme in _guide.GetSchedule(epgId, day.Date, day.Date.AddDays(1)))
        {
            var item = new GuideProgrammeViewModel(programme);
            item.RefreshState(now);
            Programmes.Add(item);
        }

        StatusText = Programmes.Count == 0
            ? $"No programmes listed for {day.Label.ToLowerInvariant()}."
            : $"{Programmes.Count} programmes.";

        // Null on any other day, which correctly leaves that list at the top.
        SelectedProgramme = Programmes.FirstOrDefault(p => p.IsAiringNow);

        OnPropertyChanged(nameof(HasProgrammes));
    }

    private void Tick()
    {
        var now = DateTimeOffset.Now;

        foreach (var programme in Programmes)
            programme.RefreshState(now);

        foreach (var channel in _allChannels)
            channel.RefreshGuide(_guide, now);
    }

    [RelayCommand]
    private void PlaySelected()
    {
        if (SelectedChannel is { } channel)
            _play(channel.Channel);
    }

    /// <summary>Jumps the day selector back to today and scrolls the list to the current programme.</summary>
    [RelayCommand]
    private void GoToNow()
    {
        SelectedDay = Days[0];
        LoadSchedule();
    }

    public void Dispose() => _timer.Stop();
}
