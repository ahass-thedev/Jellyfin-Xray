using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using Dapper;
using Jellyfin.Plugin.SceneXray.Sharing;
using Microsoft.Extensions.Options;
using Xray.Share.Server.Data;

namespace Xray.Share.Server;

/// <summary>TMDb could not be asked (no key, down, or rate-limited): the submission is retried later.</summary>
public sealed class CreditsUnavailableException(string message) : Exception(message);

/// <summary>
/// The TMDb person ids credited on a title (design §4): a movie's cast, or for an episode its cast
/// and guest stars plus the series regulars. Null when TMDb has no such title.
/// </summary>
public interface ICreditSource
{
    Task<CastList?> CastAsync(string titleKey, SharedTitle title, CancellationToken ct);
}

/// <summary>A title's credited people; <c>Ids</c> null means anyone (local testing only).</summary>
public sealed record CastList(IReadOnlySet<int>? Ids)
{
    public bool Allows(int tmdbPerson) => Ids is null || Ids.Contains(tmdbPerson);
}

/// <summary>Local testing without a TMDb key: every person passes.</summary>
public sealed class AnyCredits : ICreditSource
{
    public Task<CastList?> CastAsync(string titleKey, SharedTitle title, CancellationToken ct) =>
        Task.FromResult<CastList?>(new CastList(null));
}

/// <summary>TMDb v3, cached in <c>tmdb_credits</c> for 30 days.</summary>
public sealed class TmdbCredits : ICreditSource
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromDays(30);
    private readonly HttpClient _http;
    private readonly Db _db;
    private readonly ShareOptions _o;

    public TmdbCredits(HttpClient http, Db db, IOptions<ShareOptions> options)
    {
        _http = http;
        _db = db;
        _o = options.Value;
        _http.BaseAddress = new Uri(_o.TmdbBaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<CastList?> CastAsync(string titleKey, SharedTitle title, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        var cached = await conn.QueryFirstOrDefaultAsync<(int[] People, bool Found, DateTime FetchedAt)>(
            "select people, found, fetched_at from tmdb_credits where key = @titleKey", new { titleKey }).ConfigureAwait(false);
        if (cached.People is not null && DateTime.UtcNow - cached.FetchedAt.ToUniversalTime() < CacheFor)
        {
            return cached.Found ? new CastList(cached.People.ToHashSet()) : null;
        }

        if (string.IsNullOrWhiteSpace(_o.TmdbApiKey))
        {
            throw new CreditsUnavailableException("the TMDb credits check is not configured");
        }

        var people = await FetchAsync(title, ct).ConfigureAwait(false);
        await conn.ExecuteAsync(
            """
            insert into tmdb_credits (key, people, found, fetched_at) values (@titleKey, @people, @found, now())
            on conflict (key) do update set people = excluded.people, found = excluded.found, fetched_at = now()
            """,
            new { titleKey, people = (people ?? []).ToArray(), found = people is not null }).ConfigureAwait(false);
        return people is null ? null : new CastList(people);
    }

    private async Task<HashSet<int>?> FetchAsync(SharedTitle t, CancellationToken ct)
    {
        if (t.Kind == "movie")
        {
            int? id = t.Tmdb is > 0 ? t.Tmdb : await FindAsync(t.Imdb, "imdb_id", movie: true, ct).ConfigureAwait(false);
            var credits = id is null ? null : await GetAsync<Credits>($"movie/{id}/credits", ct).ConfigureAwait(false);
            return credits?.Cast.Select(c => c.Id).ToHashSet();
        }

        int? series = t.Series?.Tmdb is > 0
            ? t.Series.Tmdb
            : await FindAsync(t.Series?.Tvdb?.ToString(CultureInfo.InvariantCulture), "tvdb_id", movie: false, ct).ConfigureAwait(false);
        if (series is null)
        {
            return null;
        }

        var regulars = await GetAsync<Credits>($"tv/{series}/aggregate_credits", ct).ConfigureAwait(false);
        var episode = await GetAsync<Credits>($"tv/{series}/season/{t.Season}/episode/{t.Episode}/credits", ct).ConfigureAwait(false);
        if (regulars is null && episode is null)
        {
            return null;
        }

        return (regulars?.Cast ?? []).Concat(episode?.Cast ?? []).Concat(episode?.GuestStars ?? []).Select(c => c.Id).ToHashSet();
    }

    private async Task<int?> FindAsync(string? externalId, string source, bool movie, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(externalId))
        {
            return null;
        }

        var found = await GetAsync<FindResult>($"find/{Uri.EscapeDataString(externalId)}?external_source={source}", ct).ConfigureAwait(false);
        return (movie ? found?.MovieResults : found?.TvResults)?.FirstOrDefault()?.Id;
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        string url = path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api_key=" + Uri.EscapeDataString(_o.TmdbApiKey.Trim());
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!resp.IsSuccessStatusCode)
            {
                throw new CreditsUnavailableException($"TMDb answered {(int)resp.StatusCode}");
            }

            return await resp.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new CreditsUnavailableException("TMDb is unreachable");
        }
    }

    private sealed class Credits
    {
        [JsonPropertyName("cast")]
        public List<Credit> Cast { get; set; } = [];

        [JsonPropertyName("guest_stars")]
        public List<Credit> GuestStars { get; set; } = [];
    }

    private sealed class Credit
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }
    }

    private sealed class FindResult
    {
        [JsonPropertyName("movie_results")]
        public List<Credit> MovieResults { get; set; } = [];

        [JsonPropertyName("tv_results")]
        public List<Credit> TvResults { get; set; } = [];
    }
}
