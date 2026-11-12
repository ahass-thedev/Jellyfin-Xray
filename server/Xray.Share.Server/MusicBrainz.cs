using System.Net;
using System.Text.Json.Serialization;
using Dapper;
using Xray.Share.Server.Data;

namespace Xray.Share.Server;

/// <summary>A recording as MusicBrainz knows it.</summary>
public sealed record MusicBrainzRecording(string Title, string? Artist);

/// <summary>MusicBrainz could not be asked; the cue is tried again later.</summary>
public sealed class MusicBrainzUnavailableException(string message) : Exception(message);

public interface IMusicBrainz
{
    /// <summary>The recording, or null when MusicBrainz has no such id.</summary>
    Task<MusicBrainzRecording?> RecordingAsync(Guid mbid, CancellationToken ct);
}

/// <summary>
/// MusicBrainz's web service, at its required pace (one request a second) and with an identifying
/// User-Agent. Needs no key.
/// </summary>
public sealed class MusicBrainzClient : IMusicBrainz
{
    private static readonly SemaphoreSlim Pace = new(1, 1);
    private static DateTime _last = DateTime.MinValue;
    private readonly HttpClient _http;

    public MusicBrainzClient(HttpClient http)
    {
        _http = http;
        _http.BaseAddress = new Uri("https://musicbrainz.org/ws/2/");
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("XrayShare/1.0 (+https://gitlab.hassanein.me/achmadoo/Jellyfin-Xray)");
    }

    public async Task<MusicBrainzRecording?> RecordingAsync(Guid mbid, CancellationToken ct)
    {
        await Pace.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _last.AddSeconds(1.1) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            _last = DateTime.UtcNow;
            using var resp = await _http.GetAsync($"recording/{mbid:D}?inc=artist-credits&fmt=json", ct).ConfigureAwait(false);
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
            {
                return null;
            }

            if (!resp.IsSuccessStatusCode)
            {
                throw new MusicBrainzUnavailableException($"MusicBrainz answered {(int)resp.StatusCode}");
            }

            var r = await resp.Content.ReadFromJsonAsync<Recording>(ct).ConfigureAwait(false);
            return r?.Title is { Length: > 0 } title
                ? new MusicBrainzRecording(title, r.ArtistCredit.Count == 0 ? null : string.Concat(r.ArtistCredit.Select(a => a.Name + a.JoinPhrase)))
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new MusicBrainzUnavailableException("MusicBrainz is unreachable");
        }
        finally
        {
            Pace.Release();
        }
    }

    private sealed class Recording
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("artist-credit")]
        public List<Credit> ArtistCredit { get; set; } = [];
    }

    private sealed class Credit
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("joinphrase")]
        public string JoinPhrase { get; set; } = string.Empty;
    }
}

/// <summary>The <c>musicbrainz</c> job: looks up recording ids not checked yet, then requeues the consensus that names them.</summary>
public sealed class MusicBrainzService
{
    /// <summary>Lookups per run: at MusicBrainz's pace this is under a minute.</summary>
    public const int PerRun = 40;

    private readonly Db _db;
    private readonly IMusicBrainz _mb;

    public MusicBrainzService(Db db, IMusicBrainz mb)
    {
        _db = db;
        _mb = mb;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct).ConfigureAwait(false);
        var pending = (await conn.QueryAsync<Guid>(
            $"select distinct m.mbid from submission_mbids m left join mb_recordings r on r.mbid = m.mbid where r.mbid is null limit {PerRun}").ConfigureAwait(false)).ToList();
        foreach (var mbid in pending)
        {
            MusicBrainzRecording? rec;
            try
            {
                rec = await _mb.RecordingAsync(mbid, ct).ConfigureAwait(false);
            }
            catch (MusicBrainzUnavailableException)
            {
                break; // the next run tries again
            }

            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            await conn.ExecuteAsync(
                "insert into mb_recordings (mbid, found, title, artist) values (@mbid, @found, @Title, @Artist) on conflict (mbid) do nothing",
                new { mbid, found = rec is not null, rec?.Title, rec?.Artist },
                tx).ConfigureAwait(false);
            var affected = await conn.QueryAsync<(Guid VariantId, int Version)>(
                "select distinct s.variant_id, s.version from submission_mbids m join submissions s on s.id = m.submission_id where m.mbid = @mbid",
                new { mbid },
                tx).ConfigureAwait(false);
            foreach (var (v, ver) in affected)
            {
                await JobQueue.EnqueueAsync(conn, tx, JobQueue.Consensus, JobQueue.ConsensusRef(v, ver)).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        if (pending.Count == PerRun)
        {
            await JobQueue.EnqueueAsync(conn, null, JobQueue.MusicBrainz, JobQueue.AllRef, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
    }

    /// <summary>Verified recordings among <paramref name="mbids"/>.</summary>
    public static async Task<Dictionary<string, MusicBrainzRecording>> VerifiedAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction? tx, IEnumerable<string> mbids)
    {
        var ids = mbids.Select(m => Guid.TryParse(m, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }

        var rows = await conn.QueryAsync<(Guid Mbid, string Title, string? Artist)>(
            "select mbid, title, artist from mb_recordings where found and mbid = any(@ids)", new { ids }, tx).ConfigureAwait(false);
        return rows.ToDictionary(r => r.Mbid.ToString("D"), r => new MusicBrainzRecording(r.Title, r.Artist), StringComparer.OrdinalIgnoreCase);
    }
}
