using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using OpenTv.Windows.UI.Services;
using OpenTv.Windows.UI.ViewModels;

namespace OpenTv.Windows.UI.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    /// <summary>Parameterless constructor required by the Avalonia XAML previewer.</summary>
    public SettingsWindow()
        : this(new VpnViewModel(AppServices.Vpn, AppServices.VpnProfiles, new DialogService()))
    {
    }

    /// <param name="vpn">
    /// The main window's instance, so the VPN tab and the status badge share one state.
    /// </param>
    public SettingsWindow(VpnViewModel vpn)
    {
        InitializeComponent();

        _viewModel = new SettingsViewModel(vpn, new DialogService());
        DataContext = _viewModel;

        Opened += async (_, _) => await _viewModel.LoadAsync();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
