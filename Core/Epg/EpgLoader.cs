using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using WireTv.Core.Models;
using WireTv.Core.Storage;

namespace WireTv.Core.Epg;

/// <summary>
/// Fetches XMLTV guides and turns them into an <see cref="EpgGuide"/>.
///
/// Guides are large and change slowly, so the raw download is cached on disk and
/// reused until it goes stale. If a later refresh fails, the stale copy is used
/// rather than leaving the user with no guide at all.
/// </summary>
public sealed class EpgLoader : IDisposable
{
    /// <summary>Guides are big; the default HttpClient timeout is not enough.</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How much history to keep. Enough to still show the programme in progress.</summary>
    private static readonly TimeSpan PastWindow = TimeSpan.FromHours(12);

    /// <summary>How far ahead to keep. Guides rarely carry more that is accurate.</summary>
    private static readonly TimeSpan FutureWindow = TimeSpan.FromDays(8);

    public static readonly TimeSpan DefaultCacheAge = TimeSpan.FromHours(6);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public EpgLoader(HttpClient? http = null)
    {
        if (http is not null)
        {
            _http = http;
            _ownsClient = false;
            return;
        }

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true
        };

        _http = new HttpClient(handler) { Timeout = DefaultTimeout };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VLC/3.0.20 LibVLC/3.0.20");
        _ownsClient = true;
    }

    /// <summary>Where cached guide downloads are kept, so the UI can offer to clear them.</summary>
    public static string CacheDirectory => AppPaths.EnsureCacheSubDirectory("epg");

    /// <summary>
    /// Loads the guide for <paramref name="source"/>, keeping only entries for
    /// channels present in <paramref name="playlistChannels"/>.
    /// </summary>
    public async Task<EpgGuide> LoadAsync(
        string source,
        IReadOnlyList<Channel> playlistChannels,
        TimeSpan? maxCacheAge = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source))
            return EpgGuide.Empty;

        var isUrl = Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        var payload = isUrl
            ? await GetWithCacheAsync(source, maxCacheAge ?? DefaultCacheAge, ct).ConfigureAwait(false)
            : await ReadLocalFileAsync(source, ct).ConfigureAwait(false);

        var options = BuildOptions(playlistChannels);

        // Parsing tens of megabytes of XML is CPU-bound; keep it off the caller's thread.
        return await Task.Run(() =>
        {
            using var stream = OpenPossiblyCompressed(payload);
            return XmltvParser.Parse(stream, options, ct);
        }, ct).ConfigureAwait(false);
    }

    private static XmltvParseOptions BuildOptions(IReadOnlyList<Channel> playlistChannels)
    {
        var now = DateTimeOffset.Now;

        return new XmltvParseOptions
        {
            SelectChannels = guideChannels => playlistChannels.Count == 0
                ? null
                : EpgMatcher.SelectRelevantChannelIds(guideChannels, playlistChannels),
            DiscardBefore = now - PastWindow,
            DiscardAfter = now + FutureWindow
        };
    }

    private static async Task<byte[]> ReadLocalFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"EPG file not found: {path}", path);

        return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
    }

    private async Task<byte[]> GetWithCacheAsync(string url, TimeSpan maxAge, CancellationToken ct)
    {
        var cacheFile = Path.Combine(CacheDirectory, CacheKey(url) + ".xmltv");
        var cached = new FileInfo(cacheFile);

        if (cached.Exists && DateTime.UtcNow - cached.LastWriteTimeUtc < maxAge)
            return await File.ReadAllBytesAsync(cacheFile, ct).ConfigureAwait(false);

        try
        {
            var bytes = await DownloadAsync(url, ct).ConfigureAwait(false);
            await WriteCacheAsync(cacheFile, bytes, ct).ConfigureAwait(false);
            return bytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException &&
                                   !ct.IsCancellationRequested)
        {
            // A stale guide beats no guide.
            if (cached.Exists)
                return await File.ReadAllBytesAsync(cacheFile, ct).ConfigureAwait(false);

            throw new InvalidOperationException($"Could not download the EPG from {url}: {ex.Message}", ex);
        }
    }

    private async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"The EPG server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteCacheAsync(string path, byte[] bytes, CancellationToken ct)
    {
        try
        {
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to cache is not a reason to fail the load.
        }
    }

    /// <summary>
    /// Guides are commonly served as .xml.gz. Content-Encoding handles the cases
    /// where the server negotiates compression, but a gzip *file* arrives intact
    /// and has to be unwrapped here - detected by magic bytes rather than by the
    /// URL extension, which providers get wrong often enough to matter.
    /// </summary>
    private static Stream OpenPossiblyCompressed(byte[] payload)
    {
        var stream = new MemoryStream(payload, writable: false);

        if (payload.Length >= 2 && payload[0] == 0x1F && payload[1] == 0x8B)
            return new GZipStream(stream, CompressionMode.Decompress);

        return stream;
    }

    private static string CacheKey(string url)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}
