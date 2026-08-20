using System.Text.Json.Serialization;

namespace WireTv.Core.Xtream;

/// <summary>Connection details for one Xtream Codes provider.</summary>
public sealed record XtreamCredentials(string BaseUrl, string Username, string Password)
{
    /// <summary>
    /// Normalises the server address: adds a scheme when the user typed a bare
    /// host, and strips a trailing slash or an accidentally pasted player_api.php.
    /// </summary>
    public static XtreamCredentials Create(string baseUrl, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new XtreamException("No server address was given.");

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new XtreamException("Xtream Codes needs both a username and a password.");

        var trimmed = baseUrl.Trim();

        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "http://" + trimmed;

        // Users routinely paste the full API URL they got from their provider.
        var apiIndex = trimmed.IndexOf("/player_api.php", StringComparison.OrdinalIgnoreCase);
        if (apiIndex > 0)
            trimmed = trimmed[..apiIndex];

        trimmed = trimmed.TrimEnd('/');

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new XtreamException($"'{baseUrl}' is not a valid Xtream server address.");
        }

        return new XtreamCredentials(trimmed, username.Trim(), password);
    }
}

/// <summary>Account state as reported by the panel, shown on the settings screen.</summary>
public sealed record XtreamAccount(
    bool IsAuthenticated,
    string? Status,
    DateTimeOffset? ExpiresAt,
    bool IsTrial,
    int? ActiveConnections,
    int? MaxConnections,
    IReadOnlyList<string> AllowedOutputFormats,
    string? Message)
{
    /// <summary>
    /// Container to request live streams in. Providers advertise what they support;
    /// MPEG-TS is preferred because it starts faster than HLS for live channels.
    /// </summary>
    public string PreferredLiveFormat =>
        AllowedOutputFormats.Contains("ts", StringComparer.OrdinalIgnoreCase) ? "ts"
        : AllowedOutputFormats.Count > 0 ? AllowedOutputFormats[0]
        : "ts";

    public string Describe()
    {
        if (!IsAuthenticated)
            return Message is { Length: > 0 } ? $"Rejected: {Message}" : "The server rejected those credentials.";

        var parts = new List<string> { $"Status: {Status ?? "unknown"}" };

        if (ExpiresAt is { } expiry)
            parts.Add($"expires {expiry.LocalDateTime:yyyy-MM-dd}");

        if (MaxConnections is { } max)
            parts.Add($"connections {ActiveConnections ?? 0}/{max}");

        if (IsTrial)
            parts.Add("trial account");

        return string.Join(", ", parts) + ".";
    }
}

public sealed class XtreamException : Exception
{
    public XtreamException(string message) : base(message) { }

    public XtreamException(string message, Exception inner) : base(message, inner) { }
}

// ---------------------------------------------------------------------------
// Wire DTOs. Property names match the panel API exactly; every scalar goes
// through a flexible converter because panels disagree on types.
// ---------------------------------------------------------------------------

internal sealed class XtreamAuthResponse
{
    [JsonPropertyName("user_info")]
    public XtreamUserInfo? UserInfo { get; set; }
}

internal sealed class XtreamUserInfo
{
    [JsonPropertyName("auth")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? Auth { get; set; }

    [JsonPropertyName("status")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Status { get; set; }

    [JsonPropertyName("message")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Message { get; set; }

    [JsonPropertyName("exp_date")]
    [JsonConverter(typeof(FlexibleNullableLongConverter))]
    public long? ExpiryUnix { get; set; }

    [JsonPropertyName("is_trial")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? IsTrial { get; set; }

    [JsonPropertyName("active_cons")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? ActiveConnections { get; set; }

    [JsonPropertyName("max_connections")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? MaxConnections { get; set; }

    [JsonPropertyName("allowed_output_formats")]
    public List<string>? AllowedOutputFormats { get; set; }
}

internal sealed class XtreamCategory
{
    [JsonPropertyName("category_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? CategoryId { get; set; }

    [JsonPropertyName("category_name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? CategoryName { get; set; }
}

internal sealed class XtreamLiveStream
{
    [JsonPropertyName("num")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? Number { get; set; }

    [JsonPropertyName("name")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Name { get; set; }

    [JsonPropertyName("stream_id")]
    [JsonConverter(typeof(FlexibleNullableIntConverter))]
    public int? StreamId { get; set; }

    [JsonPropertyName("stream_icon")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? StreamIcon { get; set; }

    [JsonPropertyName("epg_channel_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? EpgChannelId { get; set; }

    [JsonPropertyName("category_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? CategoryId { get; set; }

    /// <summary>Set when the provider wants the client to use an explicit URL.</summary>
    [JsonPropertyName("direct_source")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? DirectSource { get; set; }
}
