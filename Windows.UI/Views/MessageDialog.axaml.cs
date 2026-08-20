using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace WireTv.Windows.UI.Views;

/// <summary>
/// Minimal message/confirm dialog. Avalonia ships no MessageBox, and a whole
/// dialog package is not worth taking on for two buttons.
/// </summary>
public partial class MessageDialog : Window
{
    private bool _result;

    public MessageDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public static async Task ShowMessageAsync(Window owner, string title, string message)
    {
        var dialog = Create(title, message, isConfirm: false);
        await dialog.ShowDialog(owner);
    }

    public static async Task<bool> ShowConfirmAsync(Window owner, string title, string message)
    {
        var dialog = Create(title, message, isConfirm: true);
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private static MessageDialog Create(string title, string message, bool isConfirm)
    {
        var dialog = new MessageDialog { Title = title };

        dialog.FindControl<TextBlock>("HeaderText")!.Text = title;
        dialog.FindControl<TextBlock>("BodyText")!.Text = message;

        if (isConfirm)
        {
            dialog.FindControl<Button>("CancelButton")!.IsVisible = true;
            dialog.FindControl<Button>("OkButton")!.Content = "Confirm";
        }

        return dialog;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        _result = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _result = false;
        Close();
    }
}
