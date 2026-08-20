using System.Net;
using System.Text;
using WireTv.Core.Models;
using WireTv.Core.Xtream;

namespace WireTv.Core.Playlists;

/// <summary>
/// Fetches playlist documents from URLs or local files and hands them to the parser.
/// Transport only: everything format-specific lives in <see cref="M3uParser"/>.
/// </summary>
public sealed class PlaylistLoader : IDisposable
{
    /// <summary>Provider playlists can be tens of megabytes; the default 100s timeout is not enough.</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(3);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public PlaylistLoader(HttpClient? http = null)
    {
        if (http is not null)
        {
            _http = http;
            _ownsClient = false;
            return;
        }

        var handler = new HttpClientHandler
        {
            // Many providers gzip their lists without being asked to negotiate it.
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true
        };

        _http = new HttpClient(handler) { Timeout = DefaultTimeout };

        // Some providers reject requests that do not look like a media player.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VLC/3.0.20 LibVLC/3.0.20");

        _ownsClient = true;
    }

    public Task<Playlist> LoadAsync(PlaylistSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (string.IsNullOrWhiteSpace(source.Location))
            throw new InvalidOperationException($"Playlist '{source.Name}' has no URL or file path configured.");

        return source.Kind switch
        {
            PlaylistSourceKind.M3uUrl => LoadFromUrlAsync(source.Location, source.Name, ct),
            PlaylistSourceKind.M3uFile => LoadFromFileAsync(source.Location, source.Name, ct),
            PlaylistSourceKind.Xtream => LoadFromXtreamAsync(source, ct),
            _ => throw new NotSupportedException($"Unknown playlist kind '{source.Kind}'.")
        };
    }

    /// <summary>
    /// Live channels from an Xtream Codes panel. The client is created per call
    /// because imports are infrequent and this keeps the loader free of extra state.
    /// </summary>
    public async Task<Playlist> LoadFromXtreamAsync(PlaylistSource source, CancellationToken ct = default)
    {
        var credentials = XtreamCredentials.Create(
            source.Location,
            source.Username ?? string.Empty,
            source.Password ?? string.Empty);

        using var client = new XtreamClient(_http);
        return await client.GetLivePlaylistAsync(credentials, source.Name, ct).ConfigureAwait(false);
    }

    public async Task<Playlist> LoadFromUrlAsync(string url, string? name = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"'{url}' is not a valid http(s) playlist URL.");
        }

        using var response = await _http
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The playlist server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await ParseAsync(stream, name, ct).ConfigureAwait(false);
    }

    public async Task<Playlist> LoadFromFileAsync(string path, string? name = null, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Playlist file not found: {path}", path);

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 64 * 1024, useAsync: true);

        return await ParseAsync(stream, name ?? Path.GetFileNameWithoutExtension(path), ct).ConfigureAwait(false);
    }

    private static async Task<Playlist> ParseAsync(Stream stream, string? name, CancellationToken ct)
    {
        // Buffer first: parsing is synchronous and CPU-bound, and doing it off a
        // network stream line by line would hold the connection open needlessly.
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;

        // detectEncodingFromByteOrderMarks handles the UTF-8 BOM some providers emit.
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        return await Task.Run(() => M3uParser.Parse(reader, name), ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}
