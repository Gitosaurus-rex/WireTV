namespace WireTv.UI.Services;

/// <summary>
/// The platform-dependent operations a ViewModel needs.
///
/// Kept as a contract in the shared layer because file pickers and modal prompts
/// look nothing alike on a desktop window and on an Android activity.
/// </summary>
public interface IDialogService
{
    Task<string?> PickFileAsync(string title, string typeName, IReadOnlyList<string> patterns);

    Task ShowMessageAsync(string title, string message);

    Task<bool> ConfirmAsync(string title, string message);
}

/// <summary>
/// Stand-in for heads that have no dialog support yet. Picking returns nothing and
/// confirmations decline, so a caller can never mistake silence for consent.
/// </summary>
public sealed class NullDialogService : IDialogService
{
    public Task<string?> PickFileAsync(string title, string typeName, IReadOnlyList<string> patterns)
        => Task.FromResult<string?>(null);

    public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(false);
}
