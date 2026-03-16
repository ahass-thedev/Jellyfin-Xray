using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.XRay.Services;

/// <summary>
/// HTTP client for the Python face recognition sidecar.
///
/// The sidecar exposes a single endpoint:
///   POST /match
///   Body: { "frame_b64": "...", "actors": [ { "name": "...", "image_path": "..." }, ... ] }
///   Response: { "matches": ["Tom Hanks", "Robin Wright"] }
///
/// The sidecar handles encoding caching internally — we just send frames and actor images.
/// </summary>
public class SidecarClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<SidecarClient> _logger;

    public SidecarClient(ILogger<SidecarClient> logger)
    {
        _logger = logger;
        var url = Plugin.Instance?.Configuration.SidecarUrl ?? "http://localhost:8756";
        _http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(30) };
    }

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Sends a single video frame (as raw bytes) and the cast image paths
    /// to the sidecar. Returns the names of matched actors.
    /// </summary>
    public async Task<IReadOnlyList<string>> MatchAsync(
        byte[] frameBytes,
        IReadOnlyList<ActorInfo> cast,
        CancellationToken ct)
    {
        var request = new MatchRequest(
            FrameB64: Convert.ToBase64String(frameBytes),
            Actors: cast
                .Where(a => a.ImagePath is not null)
                .Select(a => new ActorRequest(a.Name, a.ImagePath!))
                .ToList(),
            Tolerance: Plugin.Instance?.Configuration.FaceMatchTolerance ?? 0.55,
            ConfidenceThreshold: Plugin.Instance?.Configuration.FaceConfidenceThreshold ?? 0.60);

        try
        {
            var response = await _http
                .PostAsJsonAsync("/match", request, ct)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var result = await response.Content
                .ReadFromJsonAsync<MatchResponse>(cancellationToken: ct)
                .ConfigureAwait(false);

            return result?.Matches ?? Array.Empty<string>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Sidecar request failed — is the sidecar running at {Url}?",
                _http.BaseAddress);
            return Array.Empty<string>();
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("Sidecar request timed out");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Sends a health-check ping to the sidecar.
    /// Returns true if the sidecar is reachable.
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync("/health", ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    // ------------------------------------------------------------------
    // Request / response DTOs
    // ------------------------------------------------------------------

    private record MatchRequest(
        [property: JsonPropertyName("frame_b64")] string FrameB64,
        [property: JsonPropertyName("actors")] List<ActorRequest> Actors,
        [property: JsonPropertyName("tolerance")] double Tolerance,
        [property: JsonPropertyName("confidence_threshold")] double ConfidenceThreshold);

    private record ActorRequest(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("image_path")] string ImagePath);

    private record MatchResponse(
        [property: JsonPropertyName("matches")] List<string> Matches);
}
