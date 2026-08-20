using Avalonia.Controls;
using Avalonia.Threading;
using OpenTv.Core.Epg;
using OpenTv.Core.Models;
using OpenTv.Windows.UI.ViewModels;

namespace OpenTv.Windows.UI.Views;

public partial class GuideWindow : Window
{
    private readonly GuideViewModel? _viewModel;

    /// <summary>Parameterless constructor required by the Avalonia XAML previewer.</summary>
    public GuideWindow()
    {
        InitializeComponent();
    }

    public GuideWindow(
        EpgGuide guide,
        IReadOnlyList<ChannelListItemViewModel> channels,
        Action<Channel> play,
        ChannelListItemViewModel? initialSelection)
    {
        InitializeComponent();

        _viewModel = new GuideViewModel(guide, channels, play, initialSelection);
        DataContext = _viewModel;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // The guide ticks on a timer; stop it so a closed window is not kept alive.
        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Dispose();
        };
    }

    /// <summary>
    /// Scrolling is a view concern, so the ViewModel only names the programme it
    /// wants shown and the window brings it into view.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GuideViewModel.SelectedProgramme) || _viewModel?.SelectedProgramme is null)
            return;

        var target = _viewModel.SelectedProgramme;

        // The items have not been realised yet when the selection changes as part of
        // loading a schedule, so this waits for the layout pass to finish.
        Dispatcher.UIThread.Post(() => ProgrammeList.ScrollIntoView(target), DispatcherPriority.Background);
    }
}
