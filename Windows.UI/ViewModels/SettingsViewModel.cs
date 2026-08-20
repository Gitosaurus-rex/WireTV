using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTv.Core.Models;
using OpenTv.Core.Storage;
using OpenTv.Core.Xtream;
using OpenTv.Windows.UI.Services;

namespace OpenTv.Windows.UI.ViewModels;

/// <summary>
/// Backs the settings window: provider profiles on one tab, VPN profiles on the other.
/// The VPN tab reuses the main window's <see cref="VpnViewModel"/> instance.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly PlaylistSourceStore _store;
    private readonly IDialogService _dialogs;

    private PlaylistSourceDocument _document = new();
    private bool _loadingEditor;

    public SettingsViewModel(VpnViewModel vpn, IDialogService dialogs)
    {
        Vpn = vpn;
        _dialogs = dialogs;
        _store = AppServices.SourceStore;
    }

    public VpnViewModel Vpn { get; }

    public IReadOnlyList<PlaylistSourceKind> SourceKinds { get; } =
        [PlaylistSourceKind.M3uUrl, PlaylistSourceKind.M3uFile, PlaylistSourceKind.Xtream];

    public ObservableCollection<PlaylistSource> Sources { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private PlaylistSource? _selectedSource;

    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileSource))]
    [NotifyPropertyChangedFor(nameof(IsXtreamSource))]
    [NotifyPropertyChangedFor(nameof(LocationLabel))]
    private PlaylistSourceKind _editKind = PlaylistSourceKind.M3uUrl;

    [ObservableProperty]
    private string _editLocation = string.Empty;

    [ObservableProperty]
    private string _editUsername = string.Empty;

    [ObservableProperty]
    private string _editPassword = string.Empty;

    [ObservableProperty]
    private string _editEpgUrl = string.Empty;

    [ObservableProperty]
    private string? _feedback;

    [ObservableProperty]
    private bool _isTesting;

    public bool HasSelection => SelectedSource is not null;

    public bool IsFileSource => EditKind == PlaylistSourceKind.M3uFile;

    public bool IsXtreamSource => EditKind == PlaylistSourceKind.Xtream;

    public string LocationLabel => EditKind switch
    {
        PlaylistSourceKind.M3uFile => "File path",
        PlaylistSourceKind.Xtream => "Server address (for example http://example.com:8080)",
        _ => "Playlist URL"
    };

    public async Task LoadAsync()
    {
        _document = await _store.LoadAsync();

        Sources.Clear();
        foreach (var source in _document.Sources)
            Sources.Add(source);

        SelectedSource = Sources.FirstOrDefault();
    }

    partial void OnSelectedSourceChanged(PlaylistSource? value)
    {
        _loadingEditor = true;

        try
        {
            EditName = value?.Name ?? string.Empty;
            EditKind = value?.Kind ?? PlaylistSourceKind.M3uUrl;
            EditLocation = value?.Location ?? string.Empty;
            EditUsername = value?.Username ?? string.Empty;
            EditPassword = value?.Password ?? string.Empty;
            EditEpgUrl = value?.EpgUrl ?? string.Empty;
            Feedback = null;
        }
        finally
        {
            _loadingEditor = false;
        }
    }

    [RelayCommand]
    private void AddNew()
    {
        var source = new PlaylistSource { Name = $"Playlist {Sources.Count + 1}" };

        _document.Sources.Add(source);
        Sources.Add(source);
        SelectedSource = source;
        Feedback = "Enter a URL or pick a file, then press Save.";
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var path = await _dialogs.PickFileAsync("Select an M3U playlist", "M3U playlist", ["*.m3u", "*.m3u8"]);

        if (string.IsNullOrWhiteSpace(path))
            return;

        EditKind = PlaylistSourceKind.M3uFile;
        EditLocation = path;

        if (string.IsNullOrWhiteSpace(EditName) || EditName.StartsWith("Playlist ", StringComparison.Ordinal))
            EditName = Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// Authenticates against the Xtream panel without importing anything, so the
    /// user finds out that credentials are wrong here rather than at playback time.
    /// </summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsTesting)
            return;

        IsTesting = true;
        Feedback = "Contacting the server...";

        try
        {
            var credentials = XtreamCredentials.Create(EditLocation, EditUsername, EditPassword);

            using var client = new XtreamClient();
            var account = await client.GetAccountAsync(credentials);

            Feedback = account.Describe();
        }
        catch (XtreamException ex)
        {
            Feedback = ex.Message;
        }
        catch (Exception ex)
        {
            Feedback = $"The connection failed: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedSource is not { } source)
            return;

        var validationError = Validate();

        if (validationError is not null)
        {
            Feedback = validationError;
            return;
        }

        source.Name = EditName.Trim();
        source.Kind = EditKind;
        source.Location = EditLocation.Trim();
        source.EpgUrl = string.IsNullOrWhiteSpace(EditEpgUrl) ? null : EditEpgUrl.Trim();

        // Credentials only belong to Xtream sources; clearing them keeps a source
        // that was switched away from Xtream from holding a stale password.
        source.Username = IsXtreamSource && EditUsername.Length > 0 ? EditUsername.Trim() : null;
        source.Password = IsXtreamSource && EditPassword.Length > 0 ? EditPassword : null;

        await _store.SaveAsync(_document);

        // The list shows Name, and PlaylistSource is a plain model without change
        // notification, so the item is re-inserted to refresh its row.
        var index = Sources.IndexOf(source);

        if (index >= 0)
        {
            Sources.RemoveAt(index);
            Sources.Insert(index, source);
            SelectedSource = source;
        }

        Feedback = "Saved.";
    }

    private string? Validate()
    {
        if (string.IsNullOrWhiteSpace(EditName))
            return "Give the playlist a name.";

        var location = EditLocation.Trim();

        if (location.Length == 0)
        {
            return EditKind switch
            {
                PlaylistSourceKind.M3uFile => "Pick an M3U file.",
                PlaylistSourceKind.Xtream => "Enter the Xtream server address.",
                _ => "Enter a playlist URL."
            };
        }

        if (IsFileSource)
            return File.Exists(location) ? null : "That file does not exist.";

        if (IsXtreamSource)
        {
            if (string.IsNullOrWhiteSpace(EditUsername) || string.IsNullOrWhiteSpace(EditPassword))
                return "Xtream Codes needs both a username and a password.";

            try
            {
                // Reuses the same normalisation the loader will apply later.
                XtreamCredentials.Create(location, EditUsername, EditPassword);
                return null;
            }
            catch (XtreamException ex)
            {
                return ex.Message;
            }
        }

        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "The URL must start with http:// or https://.";
        }

        return null;
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        if (SelectedSource is not { } source)
            return;

        var confirmed = await _dialogs.ConfirmAsync("Remove playlist", $"Remove '{source.Name}'?");

        if (!confirmed)
            return;

        _document.Sources.RemoveAll(s => s.Id == source.Id);
        Sources.Remove(source);

        if (_document.LastUsedSourceId == source.Id)
            _document.LastUsedSourceId = null;

        await _store.SaveAsync(_document);

        SelectedSource = Sources.FirstOrDefault();
        Feedback = "Removed.";
    }

    partial void OnEditNameChanged(string value) => ClearFeedback();

    partial void OnEditLocationChanged(string value) => ClearFeedback();

    private void ClearFeedback()
    {
        if (!_loadingEditor)
            Feedback = null;
    }
}
