using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using WireTv.UI.Services;

namespace WireTv.Droid;

/// <summary>
/// Android implementation of <see cref="IDialogService"/>.
///
/// File picking goes through Avalonia's StorageProvider, which maps to the system
/// document picker. Message and confirmation prompts are not implemented: a modal
/// dialog is the wrong shape for a remote, so the shared UI shows those states
/// inline instead. Confirmations therefore decline rather than silently proceeding.
/// </summary>
public sealed class AndroidDialogService : IDialogService
{
    public async Task<string?> PickFileAsync(string title, string typeName, IReadOnlyList<string> patterns)
    {
        if (ResolveTopLevel() is not { } topLevel)
            return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);

    // Qualified for the same reason as App: Android.App.Application is in scope.
    private static TopLevel? ResolveTopLevel()
        => global::Avalonia.Application.Current?.ApplicationLifetime
                is ISingleViewApplicationLifetime { MainView: { } view }
            ? TopLevel.GetTopLevel(view)
            : null;
}
