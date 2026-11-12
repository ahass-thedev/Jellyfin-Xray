using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Jellyfin.Plugin.SceneXray.Sharing;

/// <summary>A non-success answer from the sharing service.</summary>
public sealed class ShareException(HttpStatusCode status, string error, string message, TimeSpan? retryAfter = null)
    : Exception($"{(int)status} {error}: {message}")
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>The service's short reason (<c>credit</c>, <c>quarantined</c>, <c>invite</c>, …).</summary>
    public string Error { get; } = error;

    /// <summary>From <c>Retry-After</c> on 429 and 503.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// The v1 API (design §6) over HTTP. Every request carries the invite's Cloudflare Access
/// service-token headers, and every route but register and health the install token.
/// </summary>
public sealed class ShareClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What every request says it is, e.g. <c>XRay-Jellyfin/1.4.0.0</c>.</summary>
    public static string UserAgent { get; set; } = "XRay-Jellyfin/" + (typeof(ShareClient).Assembly.GetName().Version?.ToString() ?? "0");
    private readonly HttpClient _http;
    private readonly Uri _base;
    private readonly Invite? _invite;
    private readonly string? _token;

    public ShareClient(HttpClient http, Uri baseUrl, Invite? invite, string? token)
    {
        _http = http;
        _base = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        _invite = invite;
        _token = token;
    }

    public Task<HealthResponse> HealthAsync(CancellationToken ct = default) => SendAsync<HealthResponse>(HttpMethod.Get, "v1/health", null, ct);

    public Task<RegisterResponse> RegisterAsync(string pluginVersion, CancellationToken ct = default) =>
        SendAsync<RegisterResponse>(HttpMethod.Post, "v1/installs", JsonContent.Create(new RegisterRequest(_invite?.Code ?? string.Empty, pluginVersion)), ct);

    public Task<InstallStatus> MeAsync(CancellationToken ct = default) => SendAsync<InstallStatus>(HttpMethod.Get, "v1/installs/me", null, ct);

    /// <summary>
    /// Variants of one title. Only the 16-bit hash prefix of the key is sent (design §2): the
    /// service answers with the whole bucket, and the title is picked out here.
    /// </summary>
    public async Task<List<VariantSummary>> AvailabilityAsync(string titleKey, CancellationToken ct = default)
    {
        string hash = TitleKey.Hash(titleKey);
        var bucket = await SendAsync<List<VariantSummary>>(HttpMethod.Get, "v1/availability?prefix=" + TitleKey.HashPrefix(titleKey), null, ct).ConfigureAwait(false);
        return bucket.Where(v => string.Equals(v.TitleKeyHash, hash, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Newer consensus for variants this install fetched, since a revision (free).</summary>
    public Task<List<VariantSummary>> UpdatesAsync(long since, CancellationToken ct = default) =>
        SendAsync<List<VariantSummary>>(HttpMethod.Get, "v1/installs/me/updates?since=" + since.ToString(CultureInfo.InvariantCulture), null, ct);

    /// <summary>Leaves the service; with <paramref name="withdraw"/>, every contribution is deleted too.</summary>
    public async Task LeaveAsync(bool withdraw, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Delete, "v1/installs/me?withdraw=" + (withdraw ? "true" : "false"), null);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new ShareException(resp.StatusCode, ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture), resp.ReasonPhrase ?? string.Empty);
        }
    }

    /// <summary>Names found for this install's unknown faces since a revision (free).</summary>
    public Task<List<UnknownName>> UnknownsAsync(long since, CancellationToken ct = default) =>
        SendAsync<List<UnknownName>>(HttpMethod.Get, "v1/installs/me/unknowns?since=" + since.ToString(CultureInfo.InvariantCulture), null, ct);

    public Task<ReferenceResponse> ReferenceAsync(Guid variantId, CancellationToken ct = default) =>
        SendAsync<ReferenceResponse>(HttpMethod.Get, $"v1/variants/{variantId}/reference", null, ct);

    /// <summary>Submits a contribution, gzipped. A rejection comes back as a result, not an exception.</summary>
    public async Task<SubmissionResult> SubmitAsync(SubmissionRequest request, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            await JsonSerializer.SerializeAsync(gz, request, Json, ct).ConfigureAwait(false);
        }

        var content = new ByteArrayContent(ms.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        try
        {
            return await SendAsync<SubmissionResult>(HttpMethod.Post, "v1/submissions", content, ct).ConfigureAwait(false);
        }
        catch (ShareException ex) when (ex.Status == HttpStatusCode.UnprocessableEntity)
        {
            return new SubmissionResult(SubmissionResult.Rejected, null, null, false, 0, 0, ex.Message);
        }
    }

    /// <summary>The variant's consensus; metered on the first fetch. Throws with <c>credit</c> when the balance is short.</summary>
    /// <param name="partial">The file is another cut, mapped piecewise (phase 5): recorded so a later local analysis still counts.</param>
    public Task<ConsensusTimeline> FetchAsync(Guid variantId, CancellationToken ct = default, bool partial = false) =>
        SendAsync<ConsensusTimeline>(HttpMethod.Post, $"v1/variants/{variantId}/fetch" + (partial ? "?partial=true" : string.Empty), null, ct);

    private HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content)
    {
        var req = new HttpRequestMessage(method, new Uri(_base, path)) { Content = content };

        // Cloudflare in front of the service blocks requests without a User-Agent, and HttpClient
        // sends none by default.
        req.Headers.UserAgent.ParseAdd(UserAgent);
        if (_invite is { AccessClientId.Length: > 0 })
        {
            req.Headers.Add("CF-Access-Client-Id", _invite.AccessClientId);
            req.Headers.Add("CF-Access-Client-Secret", _invite.AccessClientSecret);
        }

        if (!string.IsNullOrEmpty(_token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        return req;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var req = Request(method, path, content);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
        {
            return await resp.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
                ?? throw new ShareException(resp.StatusCode, "empty", "empty response");
        }

        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        ShareError? err = Parse<ShareError>(text);

        // A 422 from submissions carries a SubmissionResult with the reason.
        string message = err?.Message ?? resp.ReasonPhrase ?? string.Empty;
        if (resp.StatusCode == HttpStatusCode.UnprocessableEntity && err?.Error is null && Parse<SubmissionResult>(text)?.Reason is { } reason)
        {
            message = reason;
        }

        TimeSpan? retry = resp.Headers.RetryAfter?.Delta
            ?? (resp.Headers.TryGetValues("Retry-After", out var v) && int.TryParse(v.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out int s) ? TimeSpan.FromSeconds(s) : null);
        throw new ShareException(resp.StatusCode, err?.Error ?? ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture), message, retry);
    }

    private static TR? Parse<TR>(string text)
        where TR : class
    {
        try
        {
            return JsonSerializer.Deserialize<TR>(text, Json);
        }
        catch (JsonException)
        {
            return null; // Cloudflare's own error pages are HTML.
        }
    }
}
