using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using WireTv.UI.Services;
using WireTv.Windows.UI.Views;

namespace WireTv.Windows.UI.Services;

/// <summary>
/// Desktop implementation of <see cref="IDialogService"/>.
///
/// Resolves the owner window at call time rather than being bound to one window.
/// That matters because some ViewModels (the VPN one in particular) are shared
/// between the shell and the modal settings window: a dialog parented to the wrong
/// window would appear behind the modal one.
/// </summary>
public sealed class DialogService : IDialogService
{
    public async Task<string?> PickFileAsync(string title, string typeName, IReadOnlyList<string> patterns)
    {
        var owner = ResolveOwner();

        if (owner is null)
            return null;

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(typeName) { Patterns = [.. patterns] },
                FilePickerFileTypes.All
            ]
        });

        // TryGetLocalPath returns null for non-filesystem locations, which the
        // native pickers we use never produce - but the check keeps it honest.
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        if (ResolveOwner() is { } owner)
            await MessageDialog.ShowMessageAsync(owner, title, message);
    }

    public async Task<bool> ConfirmAsync(string title, string message)
        => ResolveOwner() is { } owner && await MessageDialog.ShowConfirmAsync(owner, title, message);

    /// <summary>The focused window, falling back to the main window.</summary>
    private static Window? ResolveOwner()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        return desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow;
    }
}
