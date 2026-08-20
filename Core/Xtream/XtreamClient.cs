using System.Net;
using System.Text.Json;
using OpenTv.Core.Models;

namespace OpenTv.Core.Xtream;

/// <summary>
/// Client for the Xtream Codes player API.
///
/// Scope is live TV, which is what the channel list needs; VOD and series use
/// separate actions and are left for later.
///
/// Note that the protocol puts the username and password in the query string -
/// that is how every Xtream panel works, not a choice made here. Because of that,
/// request URLs are redacted before they can reach an exception message or a log.
/// </summary>
public sealed class XtreamClient : IDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public XtreamClient(HttpClient? http = null)
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

    /// <summary>Authenticates and reports subscription state. Used by the "Test connection" button.</summary>
    public async Task<XtreamAccount> GetAccountAsync(XtreamCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var response = await GetAsync<XtreamAuthResponse>(credentials, action: null, ct).ConfigureAwait(false);
        var info = response?.UserInfo;

        if (info is null)
            throw new XtreamException("The server did not return any account information. Check the server address.");

        // Panels signal success with auth=1; some also set status to "Active".
        var authenticated = info.Auth == 1 ||
                            string.Equals(info.Status, "Active", StringComparison.OrdinalIgnoreCase);

        return new XtreamAccount(
            IsAuthenticated: authenticated,
            Status: info.Status,
            ExpiresAt: info.ExpiryUnix is { } unix and > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix) : null,
            IsTrial: info.IsTrial == 1,
            ActiveConnections: info.ActiveConnections,
            MaxConnections: info.MaxConnections,
            AllowedOutputFormats: info.AllowedOutputFormats ?? [],
            Message: info.Message);
    }

    /// <summary>Fetches the live categories and streams and maps them onto the shared channel model.</summary>
    public async Task<Playlist> GetLivePlaylistAsync(
        XtreamCredentials credentials,
        string? playlistName = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var account = await GetAccountAsync(credentials, ct).ConfigureAwait(false);

        if (!account.IsAuthenticated)
            throw new XtreamException(account.Describe());

        var categories = await GetAsync<List<XtreamCategory>>(credentials, "get_live_categories", ct)
            .ConfigureAwait(false) ?? [];

        var streams = await GetAsync<List<XtreamLiveStream>>(credentials, "get_live_streams", ct)
            .ConfigureAwait(false) ?? [];

        var categoryNames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var category in categories)
        {
            if (category.CategoryId is { Length: > 0 } id && category.CategoryName is { Length: > 0 } name)
                categoryNames[id] = name;
        }

        var format = account.PreferredLiveFormat;
        var channels = new List<Channel>(streams.Count);
        var warnings = new List<string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stream in streams)
        {
            if (stream.StreamId is not { } streamId)
            {
                warnings.Add($"Skipped '{stream.Name ?? "unnamed"}': the server gave no stream id.");
                continue;
            }

            // Stream ids are unique per panel, so they make the natural stable id.
            var id = $"xt-{streamId}";

            if (!seenIds.Add(id))
                continue;

            var group = stream.CategoryId is { Length: > 0 } categoryId &&
                        categoryNames.TryGetValue(categoryId, out var groupName)
                ? groupName
                : ChannelGroup.Ungrouped;

            channels.Add(new Channel
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(stream.Name) ? $"Channel {streamId}" : stream.Name!.Trim(),
                StreamUrl = BuildLiveStreamUrl(credentials, streamId, format, stream.DirectSource),
                TvgId = string.IsNullOrWhiteSpace(stream.EpgChannelId) ? null : stream.EpgChannelId,
                TvgName = stream.Name,
                LogoUrl = string.IsNullOrWhiteSpace(stream.StreamIcon) ? null : stream.StreamIcon,
                Group = group,
                Number = stream.Number
            });
        }

        return new Playlist
        {
            Name = playlistName,
            EpgUrl = BuildEpgUrl(credentials),
            Channels = channels,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Live URL layout is /live/{user}/{password}/{streamId}.{format}. A provider can
    /// override it per stream with direct_source, which is honoured when present.
    /// </summary>
    public static string BuildLiveStreamUrl(
        XtreamCredentials credentials,
        int streamId,
        string format = "ts",
        string? directSource = null)
    {
        if (!string.IsNullOrWhiteSpace(directSource) &&
            Uri.TryCreate(directSource, UriKind.Absolute, out _))
        {
            return directSource.Trim();
        }

        var user = Uri.EscapeDataString(credentials.Username);
        var password = Uri.EscapeDataString(credentials.Password);

        return $"{credentials.BaseUrl}/live/{user}/{password}/{streamId}.{format}";
    }

    /// <summary>XMLTV guide endpoint for this account, used once EPG lands in iteration 3.</summary>
    public static string BuildEpgUrl(XtreamCredentials credentials)
        => $"{credentials.BaseUrl}/xmltv.php" +
           $"?username={Uri.EscapeDataString(credentials.Username)}" +
           $"&password={Uri.EscapeDataString(credentials.Password)}";

    private async Task<T?> GetAsync<T>(XtreamCredentials credentials, string? action, CancellationToken ct)
    {
        var url = BuildApiUrl(credentials, action);

        HttpResponseMessage response;

        try
        {
            response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new XtreamException($"Could not reach {credentials.BaseUrl}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new XtreamException($"{credentials.BaseUrl} did not answer in time.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new XtreamException(
                    $"The server answered {(int)response.StatusCode} {response.ReasonPhrase} for {Redact(action)}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            try
            {
                return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                // A panel that rejects the credentials often replies with HTML or
                // a bare "false" instead of the documented JSON shape.
                throw new XtreamException(
                    $"The server did not return valid Xtream JSON for {Redact(action)}. " +
                    "Check the server address, username and password.", ex);
            }
        }
    }

    private static string BuildApiUrl(XtreamCredentials credentials, string? action)
    {
        var url = $"{credentials.BaseUrl}/player_api.php" +
                  $"?username={Uri.EscapeDataString(credentials.Username)}" +
                  $"&password={Uri.EscapeDataString(credentials.Password)}";

        return action is null ? url : url + $"&action={Uri.EscapeDataString(action)}";
    }

    /// <summary>Describes a request without ever repeating the credentials it carried.</summary>
    private static string Redact(string? action) => action is null ? "the login request" : $"action '{action}'";

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}
