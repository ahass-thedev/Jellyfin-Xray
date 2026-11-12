using System.Globalization;
using System.Net.Mime;
using System.Reflection;
using Jellyfin.Plugin.SceneXray.Analysis;
using Jellyfin.Plugin.SceneXray.Music;
using Jellyfin.Plugin.SceneXray.Services;
using Jellyfin.Plugin.SceneXray.Share;
using Jellyfin.Plugin.SceneXray.Storage;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SceneXray.Api;

/// <summary>
/// X-Ray API. Any client can use it:
///   GET  /SceneXray/Items/{id}/Timeline        everything for a title (people with scene intervals, music)
///   GET  /SceneXray/Items/{id}/At?seconds=123  just what is on screen at one moment
///   GET  /SceneXray/Items/{id}/Status          ready | queued | running | failed | none
/// Admin only: GET /SceneXray/Titles (which titles have X-Ray, and which were sent to or pulled from sharing), POST .../Analyze, POST .../Unknown/{n}/Assign?personId=, DELETE .../People/{personId}.
/// </summary>
[ApiController]
[Route("SceneXray")]
[Authorize]
public class SceneXrayController : ControllerBase
{
    private const string Admin = "RequiresElevation";
    private static readonly SemaphoreSlim TitlesGate = new(1, 1);
    private readonly AnalysisService _service;
    private readonly AnalysisQueue _queue;
    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _encoder;
    private readonly MusicService _music;
    private readonly SharingService _sharing;

    public SceneXrayController(AnalysisService service, AnalysisQueue queue, ILibraryManager library, IMediaEncoder encoder, MusicService music, SharingService sharing)
    {
        _sharing = sharing;
        _service = service;
        _queue = queue;
        _library = library;
        _encoder = encoder;
        _music = music;
    }

    /// <summary>Counts for the settings page.</summary>
    [HttpGet("Summary")]
    [Authorize(Policy = Admin)]
    public ActionResult Summary() => Ok(new
    {
        Analyzed = _service.Store.All().Count(),
        Pending = _service.Pending().Count,
        Queued = _queue.Length,
        SongsIndexed = _music.IndexedSongs,
        Device = _service.FaceDevice,
        VoiceDevice = _service.VoiceDevice,
    });

    /// <summary>
    /// Which titles have X-Ray, for the titles page: counts by state, then one page of titles.
    /// <paramref name="state"/> is all, done (up to date), outdated (has X-Ray, will be redone),
    /// missing (no X-Ray yet), active (running or queued) or failed. <paramref name="sharing"/> is
    /// sent (contributed to the sharing service), sending (in the outbox), received (pulled from
    /// it) or local (none of those).
    /// </summary>
    [HttpGet("Titles")]
    [Authorize(Policy = Admin)]
    public async Task<ActionResult> Titles([FromQuery] string? state, [FromQuery] string? search, [FromQuery] string? sharing, [FromQuery] int startIndex = 0, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        // One at a time: the first look at a large library on a network share takes a while,
        // and requests that arrive meanwhile then find the file facts remembered.
        List<TitleDto> rows;
        await TitlesGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            rows = await Task.Run(
                () =>
                {
                    var sent = _sharing.State.ContributedSubmissions().Values
                        .GroupBy(c => c.ItemId)
                        .ToDictionary(g => g.Key, g => g.Max(c => c.At));
                    var sending = _sharing.State.Outbox().Select(o => o.ItemId).ToHashSet();
                    return _service.Titles().Select(i => TitleRow(i, sent, sending)).ToList();
                },
                ct).ConfigureAwait(false);
        }
        finally
        {
            TitlesGate.Release();
        }

        var counts = rows.GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count());
        IEnumerable<TitleDto> shown = rows;
        if (!string.IsNullOrWhiteSpace(state) && state != "all")
        {
            shown = state == "active" ? shown.Where(r => r.State is "running" or "queued") : shown.Where(r => r.State == state);
        }

        if (!string.IsNullOrWhiteSpace(sharing))
        {
            shown = shown.Where(r => r.Sharing == sharing);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            shown = shown.Where(r => r.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        var list = shown
            .OrderBy(r => r.State switch { "running" => 0, "queued" => 1, "failed" => 2, _ => 3 })
            .ThenBy(r => r.State == "queued" ? r.Position : 0)
            .ThenBy(r => r.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(new
        {
            Total = rows.Count,
            Done = counts.GetValueOrDefault("done"),
            Outdated = counts.GetValueOrDefault("outdated"),
            Missing = counts.GetValueOrDefault("missing"),
            Running = counts.GetValueOrDefault("running"),
            Queued = counts.GetValueOrDefault("queued"),
            Failed = counts.GetValueOrDefault("failed"),
            Sent = rows.Count(r => r.Sharing == "sent"),
            Sending = rows.Count(r => r.Sharing == "sending"),
            Received = rows.Count(r => r.Sharing == "received"),
            Matching = list.Count,
            Items = list.Skip(Math.Max(0, startIndex)).Take(Math.Clamp(limit, 1, 500)),
        });
    }

    /// <summary>One title for the titles page.</summary>
    public sealed record TitleDto(
        Guid Id,
        string Name,
        [property: System.Text.Json.Serialization.JsonIgnore] string SortName,
        string Type,
        int? Year,
        string State,
        double Progress,
        int Position,
        string? Error,
        DateTime? AnalyzedAt,
        bool Shared,
        int People,
        int Voices,
        int Songs,
        string Sharing,
        DateTime? SharedAt);

    private TitleDto TitleRow(MediaBrowser.Controller.Entities.BaseItem item, Dictionary<Guid, DateTime> sent, HashSet<Guid> sending)
    {
        var head = _service.Store.Head(item.Id);
        var active = _queue.Active(item.Id);
        string state = active?.State ?? (head is null ? "missing" : _service.IsCurrent(head, item) ? "done" : "outdated");
        // A received timeline replaces whatever was sent for the title before.
        DateTime? sharedAt = null;
        string sharingState = "local";
        if (head is { Shared: true })
        {
            (sharingState, sharedAt) = ("received", head.ReceivedAt);
        }
        else if (sending.Contains(item.Id))
        {
            sharingState = "sending";
        }
        else if (sent.TryGetValue(item.Id, out var at))
        {
            (sharingState, sharedAt) = ("sent", at);
        }

        string name = item.Name, sortName = item.SortName ?? item.Name;
        if (item is MediaBrowser.Controller.Entities.TV.Episode e)
        {
            string number = e.ParentIndexNumber is { } s && e.IndexNumber is { } n
                ? string.Format(CultureInfo.InvariantCulture, " S{0:00}E{1:00}", s, n)
                : string.Empty;
            name = e.SeriesName + number + " \u00b7 " + e.Name;
            sortName = string.Format(CultureInfo.InvariantCulture, "{0} {1:0000} {2:0000}", e.SeriesName, e.ParentIndexNumber ?? 0, e.IndexNumber ?? 0);
        }

        return new TitleDto(
            item.Id,
            name,
            sortName,
            item is MediaBrowser.Controller.Entities.TV.Episode ? "Episode" : "Movie",
            item.ProductionYear,
            state,
            active?.Progress ?? 0,
            active?.Position ?? 0,
            active?.Error,
            head?.AnalyzedAt,
            head?.Shared ?? false,
            head?.People ?? 0,
            head?.VoicePeople ?? 0,
            head?.Songs ?? 0,
            sharingState,
            sharedAt);
    }

    private bool IsAdmin => User.IsInRole("Administrator");

    [HttpGet("Items/{itemId}/Timeline")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetTimeline([FromRoute] Guid itemId)
    {
        var t = _service.Store.Get(itemId);
        if (t is null)
        {
            return NotFound(_queue.Status(itemId));
        }

        return Ok(Project(t, IsAdmin));
    }

    [HttpGet("Items/{itemId}/At")]
    public ActionResult GetAt([FromRoute] Guid itemId, [FromQuery] double seconds)
    {
        var t = _service.Store.Get(itemId);
        if (t is null)
        {
            return NotFound(_queue.Status(itemId));
        }

        return Ok(new
        {
            seconds,
            // On screen right now first, then the rest of the scene (cutaways, voices), each by screen time.
            people = Merged(t)
                .Where(p => Covers(p.Intervals, seconds))
                .OrderByDescending(p => Covers(p.Spans!, seconds))
                .Select(p => PersonDto(p, seconds)),
            music = t.Music.Where(m => seconds >= m.Start && seconds <= m.End),
        });
    }

    [HttpGet("Items/{itemId}/Status")]
    public ActionResult<QueueStatus> GetStatus([FromRoute] Guid itemId) => _queue.Status(itemId);

    [HttpPost("Items/{itemId}/Analyze")]
    [Authorize(Policy = Admin)]
    public ActionResult<QueueStatus> Analyze([FromRoute] Guid itemId)
    {
        if (!AnalysisService.IsEligible(_library.GetItemById(itemId)))
        {
            return BadRequest("Only movies and episodes with a media file can be analysed.");
        }

        _ = _queue.Enqueue(itemId, QueuePriority.Manual);
        return _queue.Status(itemId);
    }

    /// <summary>Joins the sharing service with an invite, then turns sharing on (design §1).</summary>
    [HttpPost("Sharing/Join")]
    [Authorize(Policy = Admin)]
    public async Task<ActionResult> JoinSharing([FromBody] JoinRequest body, CancellationToken ct)
    {
        string? error = await _sharing.JoinAsync(body.Invite ?? string.Empty, ct).ConfigureAwait(false);
        if (error is not null)
        {
            return BadRequest(new { Error = error });
        }

        var plugin = Plugin.Instance!;
        plugin.Configuration.SharingEnabled = true;
        plugin.SaveConfiguration();
        return Ok(new { Joined = true });
    }

    /// <summary>For the settings page: membership and, when sharing is on, the service's status line.</summary>
    [HttpGet("Sharing/Status")]
    [Authorize(Policy = Admin)]
    public async Task<ActionResult> SharingStatus(CancellationToken ct)
    {
        var status = await _sharing.StatusAsync(ct).ConfigureAwait(false);
        return Ok(new
        {
            Joined = _sharing.State.Membership is not null,
            Enabled = Plugin.Instance!.Configuration.SharingEnabled,
            Status = status,
            Received = _sharing.ReceivedTitles().Count,
        });
    }

    /// <summary>Leave, or leave and withdraw every contribution (design §5).</summary>
    [HttpPost("Sharing/Leave")]
    [Authorize(Policy = Admin)]
    public async Task<ActionResult> LeaveSharing([FromQuery] bool withdraw, CancellationToken ct)
    {
        string? error = await _sharing.LeaveAsync(withdraw, ct).ConfigureAwait(false);
        return error is null ? Ok(new { Left = true }) : BadRequest(new { Error = error });
    }

    /// <summary>Queues every received title for local analysis, replacing the shared copy (design §5).</summary>
    [HttpPost("Sharing/ReanalyseReceived")]
    [Authorize(Policy = Admin)]
    public ActionResult ReanalyseReceived()
    {
        var ids = _sharing.ReceivedTitles();
        foreach (var id in ids)
        {
            _ = _queue.Enqueue(id, QueuePriority.Manual);
        }

        return Ok(new { Queued = ids.Count });
    }

    public sealed class JoinRequest
    {
        public string? Invite { get; set; }
    }

    [HttpPost("Items/{itemId}/Unknown/{unknownId}/Assign")]
    [Authorize(Policy = Admin)]
    public ActionResult Assign([FromRoute] Guid itemId, [FromRoute] int unknownId, [FromQuery] Guid personId)
    {
        var t = _service.AssignUnknown(itemId, unknownId, personId);
        return t is null ? NotFound() : Ok(Project(t, true));
    }

    [HttpDelete("Items/{itemId}/People/{personId}")]
    [Authorize(Policy = Admin)]
    public ActionResult Remove([FromRoute] Guid itemId, [FromRoute] Guid personId)
    {
        var t = _service.RemovePerson(itemId, personId);
        return t is null ? NotFound() : Ok(Project(t, true));
    }

    /// <summary>A face crop for an unknown person, so an admin can see who it is.</summary>
    [HttpGet("Items/{itemId}/Unknown/{unknownId}/Thumb")]
    [Authorize(Policy = Admin)]
    public async Task<ActionResult> Thumb([FromRoute] Guid itemId, [FromRoute] int unknownId, CancellationToken ct)
    {
        var t = _service.Store.Get(itemId);
        var u = t?.Unknown.FirstOrDefault(x => x.Id == unknownId);
        var item = _library.GetItemById(itemId);
        if (u is null || item is null || u.ThumbBox.Length != 4)
        {
            return NotFound();
        }

        string dir = Path.Combine(Plugin.Instance!.DataDirectory, "thumbs");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"{itemId:N}-{unknownId}-{t!.AnalyzedAt.Ticks}.jpg");
        if (!System.IO.File.Exists(file))
        {
            // Pad the box by 40% so hair and chin are visible, clamp to the frame.
            float w = u.ThumbBox[2] - u.ThumbBox[0], h = u.ThumbBox[3] - u.ThumbBox[1];
            float x = Math.Max(0, u.ThumbBox[0] - (0.4f * w)), y = Math.Max(0, u.ThumbBox[1] - (0.4f * h));
            float cw = Math.Min(1 - x, w * 1.8f), ch = Math.Min(1 - y, h * 1.8f);
            string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
            var (code, _, _) = await FrameSource.RunAsync(
                _encoder.EncoderPath,
                ["-hide_banner", "-loglevel", "error", "-nostdin", "-ss", u.ThumbTime.ToString(CultureInfo.InvariantCulture), "-i", item.Path,
                 "-frames:v", "1", "-vf", $"crop=iw*{F(cw)}:ih*{F(ch)}:iw*{F(x)}:ih*{F(y)},scale=200:-2", "-y", file],
                ct).ConfigureAwait(false);
            if (code != 0 || !System.IO.File.Exists(file))
            {
                return NotFound();
            }
        }

        return PhysicalFile(file, MediaTypeNames.Image.Jpeg);
    }

    [HttpGet("overlay.js")]
    [AllowAnonymous]
    public ActionResult OverlayScript() => Resource("Web.overlay.js", "application/javascript");

    [HttpGet("overlay.css")]
    [AllowAnonymous]
    public ActionResult OverlayStyle() => Resource("Web.overlay.css", "text/css");

    private ActionResult Resource(string name, string type)
    {
        var asm = Assembly.GetExecutingAssembly();
        var stream = asm.GetManifestResourceStream(typeof(Plugin).Namespace + "." + name);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=3600";
        return File(stream, type + "; charset=utf-8");
    }

    /// <summary>One entry per person: on-screen (face) and speaking (voice) time combined.</summary>
    private static List<TimelinePerson> Merged(XrayTimeline t)
    {
        var people = new List<TimelinePerson>();
        foreach (var g in t.People.Concat(t.VoicePeople).GroupBy(p => p.PersonId))
        {
            var face = g.FirstOrDefault(p => !p.Source.StartsWith("voice", StringComparison.Ordinal));
            var voice = g.FirstOrDefault(p => p.Source.StartsWith("voice", StringComparison.Ordinal));
            var first = face ?? voice!;
            // Seen for a moment or heard once in a scene: in the whole scene.
            var intervals = t.Scenes.Count == 0
                ? ScenePresence.Union(g.SelectMany(p => p.Intervals))
                : ScenePresence.Union(g.SelectMany(p => ScenePresence.Expand(
                    p.Spans ?? p.Intervals, t.Scenes, heard: p.Source.StartsWith("voice", StringComparison.Ordinal))));
            people.Add(new TimelinePerson
            {
                PersonId = first.PersonId,
                Name = first.Name,
                Role = face?.Role ?? voice?.Role,
                ScreenTimeSeconds = face?.ScreenTimeSeconds ?? voice!.ScreenTimeSeconds,
                Confidence = g.Max(p => p.Confidence),
                Intervals = intervals,
                // Where the face itself is seen, so a client can tell on screen from in scene. None for a voice alone.
                Spans = face is null ? [] : ScenePresence.Union(face.Spans ?? face.Intervals),
                Source = face is not null && voice is not null ? "face+voice" : first.Source.StartsWith("voice", StringComparison.Ordinal) ? "voice" : "face",
            });
        }

        return people.OrderByDescending(p => p.ScreenTimeSeconds).ToList();
    }

    private static bool IsVoiceRole(string? role) => role?.Contains("(voice)", StringComparison.OrdinalIgnoreCase) == true;

    private static bool Covers(List<double[]> intervals, double seconds) => intervals.Any(i => seconds >= i[0] && seconds <= i[1]);

    /// <summary>With <paramref name="at"/>, also says whether the face is on screen at that moment.</summary>
    private static object PersonDto(TimelinePerson p, double? at) => new
    {
        p.PersonId,
        p.Name,
        Role = CleanRole(p.Role),
        VoiceRole = IsVoiceRole(p.Role),
        p.Source,
        p.ScreenTimeSeconds,
        p.Confidence,
        p.Intervals,
        p.Spans,
        OnScreen = at is { } s ? Covers(p.Spans!, s) : (bool?)null,
        ImageUrl = $"Items/{p.PersonId:N}/Images/{ImageType.Primary}",
    };

    /// <summary>"Buddy / Dark Buddy (voice)" -> "Buddy / Dark Buddy"; "(voice)" alone -> null.</summary>
    private static string? CleanRole(string? role)
    {
        if (role is null)
        {
            return null;
        }

        var r = System.Text.RegularExpressions.Regex.Replace(role, @"\s*\((?:voice|uncredited)\)", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        return r.Length == 0 ? null : r;
    }

    /// <summary>Everyone credited, so a client can list the (voice) cast even before anyone is placed in a scene.</summary>
    private IEnumerable<object> Credits(Guid itemId)
    {
        var item = _library.GetItemById(itemId);
        if (item is null)
        {
            return [];
        }

        var credits = new List<MediaBrowser.Controller.Entities.PersonInfo>(_library.GetPeople(item));
        if (item is MediaBrowser.Controller.Entities.TV.Episode { Series: { } series })
        {
            credits.AddRange(_library.GetPeople(series));
        }

        return credits
            .Where(p => p.Type is Jellyfin.Data.Enums.PersonKind.Actor or Jellyfin.Data.Enums.PersonKind.GuestStar)
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(40)
            .Select(p =>
            {
                Guid id = _library.GetPerson(p.Name)?.Id ?? p.Id;
                return (object)new { PersonId = id, p.Name, Role = CleanRole(p.Role), VoiceRole = IsVoiceRole(p.Role), ImageUrl = $"Items/{id:N}/Images/{ImageType.Primary}" };
            });
    }

    private object Project(XrayTimeline t, bool admin) => new
    {
        t.SchemaVersion,
        t.ItemId,
        t.AnalyzedAt,
        t.DurationSeconds,
        t.Analyzer,
        People = Merged(t).Select(p => PersonDto(p, null)),
        Cast = Credits(t.ItemId),
        t.Music,
        t.MusicAnalyzed,
        Unknown = admin
            ? t.Unknown.Select(u => new { u.Id, u.ScreenTimeSeconds, u.Intervals, u.SuggestedPersonId, ThumbUrl = $"SceneXray/Items/{t.ItemId:N}/Unknown/{u.Id}/Thumb" })
            : [],
        t.Stats,
        t.Voice,
    };
}
