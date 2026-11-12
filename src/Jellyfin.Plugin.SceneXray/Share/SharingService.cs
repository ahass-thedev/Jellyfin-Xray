using System.Net;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SceneXray.Configuration;
using Jellyfin.Plugin.SceneXray.Services;
using Jellyfin.Plugin.SceneXray.Sharing;
using Jellyfin.Plugin.SceneXray.Storage;
using Jellyfin.Plugin.SceneXray.Vision;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SceneXray.Share;

/// <summary>
/// The plugin side of shared X-Ray data (docs/shared-data-design.md §5). With sharing on, a title
/// is first looked up on the service; when a variant fits this file, credit allows and the tier
/// meets the bar, its consensus is fetched instead of analysing. Otherwise it is analysed locally
/// and the result submitted in the background. Any failure of the service falls back to local
/// analysis and never blocks the queue. With sharing off, nothing here touches the network.
/// </summary>
public sealed class SharingService
{
    private readonly ILibraryManager _library;
    private readonly AnalysisService _analysis;
    private readonly FingerprintCache _fingerprints;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SharingService> _log;
    private DateTime _pausedUntil = DateTime.MinValue;

    public SharingService(ILibraryManager library, AnalysisService analysis, FingerprintCache fingerprints, IHttpClientFactory http, ILogger<SharingService> log)
    {
        _library = library;
        _analysis = analysis;
        _fingerprints = fingerprints;
        _http = http;
        _log = log;
        State = new ShareState(Path.Combine(Plugin.Instance!.DataDirectory, "share"));
        ShareClient.UserAgent = "XRay-Jellyfin/" + PluginVersion;
        Plugin.Instance.ConfigurationChanged += (_, _) => OnConfigurationChanged();
    }

    public ShareState State { get; }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>Sharing is on only with the toggle and a membership. One opt-in, both ways.</summary>
    public bool Active => Config.SharingEnabled && State.Membership is not null;

    public static string PluginVersion => typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";

    private ShareClient Client(Membership? m, Invite? invite = null)
    {
        var http = _http.CreateClient("xray-share");
        http.Timeout = TimeSpan.FromSeconds(60);
        invite ??= m is null ? null : new Invite(string.Empty, m.AccessClientId, m.AccessClientSecret);
        return new ShareClient(http, new Uri(Config.SharingServerUrl), invite, m?.Token);
    }

    /// <summary>Turning the toggle off stops everything at once: queued submissions are discarded.</summary>
    private void OnConfigurationChanged()
    {
        if (!Config.SharingEnabled)
        {
            State.ClearOutbox();
            _fingerprints.Clear();
        }
    }

    /// <summary>Registers with an invite (design §1). Null on success, else why not.</summary>
    public async Task<string?> JoinAsync(string inviteText, CancellationToken ct)
    {
        if (Invite.TryParse(inviteText) is not { } invite)
        {
            return "That is not an X-Ray invite. It starts with \"xray1.\".";
        }

        try
        {
            var r = await Client(null, invite).RegisterAsync(PluginVersion, ct).ConfigureAwait(false);
            State.SaveMembership(new Membership { Token = r.Token, AccessClientId = invite.AccessClientId, AccessClientSecret = invite.AccessClientSecret, JoinedAt = DateTime.UtcNow });
            return null;
        }
        catch (ShareException ex)
        {
            return ex.Error switch
            {
                "invite" => "The sharing service doesn't know this invite.",
                "invite-used" => "This invite has already been used by its maximum number of servers.",
                "plugin-too-old" => "Update X-Ray first: " + ex.Message,
                _ => "The sharing service refused: " + ex.Message,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "Could not reach the sharing service: " + ex.Message;
        }
    }

    /// <summary>
    /// Leave (design §5): the service deletes this install, its balance, its title links and its
    /// unknown-face embeddings; with <paramref name="withdraw"/>, its contributions too. Then the
    /// local token goes and sharing is off. Received timelines stay. Null on success.
    /// </summary>
    public async Task<string?> LeaveAsync(bool withdraw, CancellationToken ct)
    {
        if (State.Membership is not { } m)
        {
            return "This server is not a member.";
        }

        try
        {
            await Client(m).LeaveAsync(withdraw, ct).ConfigureAwait(false);
        }
        catch (ShareException ex) when (ex.Status == HttpStatusCode.Unauthorized)
        {
            // Already gone (deleted after a year of silence, say): leaving locally is all that is left.
        }
        catch (Exception ex) when (ex is ShareException or HttpRequestException or TaskCanceledException)
        {
            return "Could not reach the sharing service, so nothing was deleted there. Try again later. (" + ex.Message + ")";
        }

        State.ClearMembership();
        _fingerprints.Clear();
        var plugin = Plugin.Instance!;
        plugin.Configuration.SharingEnabled = false;
        plugin.SaveConfiguration();
        _log.LogInformation("X-Ray sharing: left the sharing service{Withdraw}", withdraw ? " and withdrew every contribution" : string.Empty);
        return null;
    }

    /// <summary>Titles whose timeline came from the sharing service.</summary>
    public List<Guid> ReceivedTitles() =>
        _analysis.Store.All().Where(id => _analysis.Store.Get(id) is { } t && ShareMapping.IsShared(t.Signature)).ToList();

    /// <summary>
    /// Refresh (design §5 step 4): the service names the received variants whose consensus moved
    /// (a newer revision, or a newer analyser now confirmed); those titles are fetched again, for
    /// free, and mapped through the fit they were received with.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!Active || Paused)
        {
            return;
        }

        try
        {
            var client = Client(State.Membership);
            var moved = await client.UpdatesAsync(State.UpdatesSince, ct).ConfigureAwait(false);
            if (moved.Count == 0)
            {
                return;
            }

            var byVariant = _analysis.Store.All()
                .Select(id => _analysis.Store.Get(id))
                .Where(t => t?.Shared is not null && ShareMapping.IsShared(t.Signature))
                .GroupBy(t => t!.Shared!.VariantId)
                .ToDictionary(g => g.Key, g => g.Select(t => t!).ToList());
            foreach (var v in moved.OrderBy(v => v.Revision))
            {
                if (byVariant.TryGetValue(v.VariantId, out var local))
                {
                    var consensus = await client.FetchAsync(v.VariantId, ct).ConfigureAwait(false);
                    foreach (var t in local)
                    {
                        if (_library.GetItemById(t.ItemId) is Video item && AnalysisService.IsSharedCurrent(t, item))
                        {
                            Save(item, consensus, t.Shared!, new FileInfo(item.Path));
                            _log.LogInformation("X-Ray sharing: refreshed {Name} (revision {Revision}, {Tier})", item.Name, consensus.Revision, consensus.Tier);
                        }
                    }
                }

                State.UpdatesSince = Math.Max(State.UpdatesSince, v.Revision);
            }
        }
        catch (Exception ex) when (ex is ShareException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Note(ex, "refresh");
        }
    }

    public async Task<InstallStatus?> StatusAsync(CancellationToken ct)
    {
        if (!Active)
        {
            return null;
        }

        try
        {
            return await Client(State.Membership).MeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ShareException or HttpRequestException or TaskCanceledException)
        {
            _log.LogDebug(ex, "X-Ray sharing: status unavailable");
            return null;
        }
    }

    private bool Paused => DateTime.UtcNow < _pausedUntil;

    private void Note(Exception ex, string what)
    {
        if (ex is ShareException { Status: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable } se)
        {
            _pausedUntil = DateTime.UtcNow + (se.RetryAfter ?? TimeSpan.FromMinutes(10));
        }

        _log.LogInformation("X-Ray sharing: {What} failed, analysing locally: {Message}", what, ex.Message);
    }

    /// <summary>
    /// Before analysing (design §5 step 1–2): fetch the title's consensus when a variant fits this
    /// file. Unconfirmed data is taken only for someone waiting to watch; otherwise the title is
    /// analysed locally, which earns credit and confirms the variant (design D4). True when a
    /// timeline was received and saved.
    /// </summary>
    public async Task<bool> TryReceiveAsync(Guid itemId, QueuePriority priority, CancellationToken ct)
    {
        if (!Active || Paused || priority == QueuePriority.Manual || _library.GetItemById(itemId) is not Video item || !AnalysisService.IsEligible(item))
        {
            return false;
        }

        if (TitleOf(item) is not { } title || TitleKey.Of(title) is not { } key)
        {
            return false;
        }

        try
        {
            var client = Client(State.Membership);
            var variants = await client.AvailabilityAsync(key, ct).ConfigureAwait(false);
            if (variants.Count == 0)
            {
                return false;
            }

            var match = await MatchAsync(client, item, variants, title.Runtime, ct).ConfigureAwait(false);
            if (match is null)
            {
                if (priority == QueuePriority.Playback && Config.SharingUseOtherCuts && await TryReceivePartialAsync(client, item, variants, title.Runtime, ct).ConfigureAwait(false))
                {
                    return true;
                }

                _log.LogInformation("X-Ray sharing: no shared variant fits {Name}; analysing locally", item.Name);
                return false;
            }

            if (match.Variant.Tier != ConsensusTimeline.Confirmed && priority != QueuePriority.Playback)
            {
                return false;
            }

            var consensus = await client.FetchAsync(match.Variant.VariantId, ct).ConfigureAwait(false);
            Save(item, consensus, match);
            _fingerprints.Drop(item.Id);
            _log.LogInformation("X-Ray sharing: received {Name} ({Tier}, {Voices} voices)", item.Name, consensus.Tier, consensus.Voices);
            return true;
        }
        catch (ShareException ex) when (ex.Error is "credit" or "quarantined")
        {
            _log.LogInformation("X-Ray sharing: {Name} not fetched ({Reason}); analysing locally", item.Name, ex.Error);
            return false;
        }
        catch (Exception ex) when (ex is ShareException or HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException && !ct.IsCancellationRequested)
        {
            Note(ex, "lookup of " + item.Name);
            return false;
        }
    }

    /// <summary>
    /// Another cut (phase 5): a variant whose scenes this file shares, mapped piecewise, with
    /// ±60 s dropped around each splice. Only for someone waiting to watch; the title stays
    /// queued and is analysed here later, which contributes it as its own variant.
    /// </summary>
    private async Task<bool> TryReceivePartialAsync(ShareClient client, Video item, IReadOnlyList<VariantSummary> variants, double runtime, CancellationToken ct)
    {
        int track = AudioTracks(item)[0];
        var fp = await _fingerprints.GetAsync(item, track, null, ct).ConfigureAwait(false);
        foreach (var v in variants)
        {
            var reference = await client.ReferenceAsync(v.VariantId, ct).ConfigureAwait(false);
            var pw = PiecewiseAligner.Align(reference.ToSet(), fp, reference.Runtime, runtime);
            if (!pw.Accepted)
            {
                continue;
            }

            var consensus = await client.FetchAsync(v.VariantId, ct, partial: true).ConfigureAwait(false);
            var info = new FileInfo(item.Path);
            var fit = new SharedInfo
            {
                VariantId = consensus.VariantId,
                Slope = pw.Slope,
                ValidFrom = pw.Segments[0].CanonicalFrom,
                ValidTo = pw.Segments[^1].CanonicalTo,
                Segments = pw.Segments.Select(s => new[] { s.CanonicalFrom, s.CanonicalTo, s.Offset }).ToList(),
                ReceivedAt = DateTime.UtcNow,
            };
            Save(item, consensus, fit, info, partial: true);
            _log.LogInformation("X-Ray sharing: received {Name} partially, from another cut ({Verdict}); it will still be analysed here", item.Name, pw.Verdict);
            return true;
        }

        return false;
    }

    private void Save(Video item, ConsensusTimeline c, VariantMatch match)
    {
        var info = new FileInfo(item.Path);
        var fit = new SharedInfo
        {
            VariantId = c.VariantId,
            Revision = c.Revision,
            Version = c.Version,
            Tier = c.Tier,
            Slope = match.Fit.Slope,
            Offset = match.Fit.Offset,
            ValidFrom = match.Fit.ValidFrom,
            ValidTo = match.Fit.ValidTo,
            ReceivedAt = DateTime.UtcNow,
        };
        Save(item, c, fit, info);
    }

    private void Save(Video item, ConsensusTimeline c, SharedInfo fit, FileInfo info, bool partial = false)
    {
        fit.Revision = c.Revision;
        fit.Tier = c.Tier;
        fit.Version = c.Version;
        var previous = _analysis.Store.Get(item.Id);
        var t = ShareMapping.Timeline(
            c,
            fit,
            item.Id,
            Runtime(item),
            partial
                ? ShareMapping.PartialSignature(c.VariantId, c.Revision, info.Length, info.LastWriteTimeUtc.Ticks)
                : ShareMapping.SharedSignature(c.VariantId, c.Revision, info.Length, info.LastWriteTimeUtc.Ticks),
            CastByTmdb(item),
            SongByMbid,
            previous);
        t.VoiceSignature = AnalysisService.VoiceSignature(item);
        t.SceneSignature = AnalysisService.SceneSignature(item);
        _analysis.Store.Save(t);
    }

    /// <summary>Queues a title's local result for submission (with retries, in the background).</summary>
    public void Offer(Guid itemId)
    {
        if (Active)
        {
            State.Enqueue(itemId, DateTime.UtcNow);
        }
    }

    /// <summary>Outcome of one submission attempt.</summary>
    public enum SubmitOutcome
    {
        /// <summary>Accepted, derived, or rejected for good: nothing more to do.</summary>
        Done,

        /// <summary>The service or network failed: try again later.</summary>
        Retry,
    }

    public async Task<SubmitOutcome> SubmitAsync(Guid itemId, CancellationToken ct)
    {
        if (!Active)
        {
            return SubmitOutcome.Done;
        }

        if (Paused)
        {
            return SubmitOutcome.Retry;
        }

        var t = _analysis.Store.Get(itemId);
        if (t is null || ShareMapping.IsShared(t.Signature) || _library.GetItemById(itemId) is not Video item || !AnalysisService.IsEligible(item)
            || TitleOf(item) is not { } title || TitleKey.Of(title) is not { } key)
        {
            return SubmitOutcome.Done;
        }

        try
        {
            var client = Client(State.Membership);
            var variants = await client.AvailabilityAsync(key, ct).ConfigureAwait(false);
            var match = variants.Count == 0 ? null : await MatchAsync(client, item, variants, title.Runtime, ct).ConfigureAwait(false);
            int track = match?.AudioTrack ?? AudioTracks(item).FirstOrDefault();
            var fp = await _fingerprints.GetAsync(item, track, null, ct).ConfigureAwait(false);
            var anchors = AnchorSet.Extract(fp, AnchorSet.EverySecondsFor(title.Runtime));
            var contribution = ShareMapping.Contribution(t, title, Analyzer(), anchors, TmdbOf, MbidOf, UnknownFaces(t));
            var request = new SubmissionRequest(contribution, match?.Variant.VariantId, match?.ToSubmissionFit(), match?.Answer());
            var result = await client.SubmitAsync(request, ct).ConfigureAwait(false);
            if (result.SubmissionId is { } sid)
            {
                State.RecordContribution(sid, itemId, t.Signature);
            }

            if (result.Status == SubmissionResult.Rejected)
            {
                _log.LogWarning("X-Ray sharing: {Name} was rejected: {Reason}", item.Name, result.Reason);
            }
            else
            {
                _log.LogInformation("X-Ray sharing: contributed {Name} ({Status}, +{Credit} credit, {Held} held)", item.Name, result.Status, result.Credit, result.Held);
            }

            _fingerprints.Drop(itemId);
            return SubmitOutcome.Done;
        }
        catch (Exception ex) when (ex is ShareException or HttpRequestException or TaskCanceledException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            Note(ex, "submission of " + item.Name);
            return SubmitOutcome.Retry;
        }
    }

    /// <summary>The timeline's unknown faces, with the recognizer of the pack that analysed it.</summary>
    private static List<SharedUnknownFace> UnknownFaces(XrayTimeline t) =>
        Enum.TryParse<ModelPack>(t.Analyzer.ModelPack, out var pack)
            ? ShareMapping.UnknownFaces(t, Path.GetFileNameWithoutExtension(ModelStore.FilesFor(pack).Recognizer), t.DurationSeconds)
            : [];

    /// <summary>
    /// Applies names the service found for this install's own unknown faces (design §2): through
    /// the same path as an admin naming one, marked as coming from sharing. The gallery learns
    /// only when at least two independent voices back the name (design D15). Free.
    /// </summary>
    public async Task SyncUnknownsAsync(CancellationToken ct)
    {
        if (!Active || Paused)
        {
            return;
        }

        List<UnknownName> names;
        try
        {
            names = await Client(State.Membership).UnknownsAsync(State.UnknownsSince, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ShareException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            Note(ex, "fetching names for unknown faces");
            return;
        }

        var contributed = State.ContributedSubmissions();
        foreach (var n in names.OrderBy(n => n.Revision))
        {
            if (contributed.TryGetValue(n.SubmissionId, out var c) && _analysis.Store.Get(c.ItemId) is { } t && t.Signature == c.Signature
                && _library.GetItemById(c.ItemId) is { } item && CastByTmdb(item).TryGetValue(n.TmdbPerson, out var person))
            {
                bool teach = n.Voices >= 2;
                if (t.Unknown.Any(u => u.Id == n.UnknownId))
                {
                    _analysis.AssignUnknown(c.ItemId, n.UnknownId, person.PersonId, "sharing", n.Confidence, teach);
                    _log.LogInformation("X-Ray sharing: named an unknown face in {Name}: {Person} ({Voices} voices)", item.Name, person.Name, n.Voices);
                }
                else if (teach)
                {
                    _analysis.TeachSharedName(c.ItemId, person.PersonId);
                }
            }

            State.UnknownsSince = Math.Max(State.UnknownsSince, n.Revision);
        }
    }

    private Task<VariantMatch?> MatchAsync(ShareClient client, Video item, IReadOnlyList<VariantSummary> variants, double runtime, CancellationToken ct) =>
        VariantMatcher.FindAsync(
            client.ReferenceAsync,
            variants,
            AudioTracks(item),
            (track, filter, c) => _fingerprints.GetAsync(item, track, filter, c),
            runtime,
            ct: ct);

    private static SharedAnalyzer Analyzer()
    {
        var cfg = Config;
        return new SharedAnalyzer(
            Plugin.AnalyzerVersion,
            PluginVersion,
            cfg.ModelPack.ToString(),
            cfg.SampleFps,
            cfg.VoicesEnabled ? Path.GetFileNameWithoutExtension(ModelStore.SpeakerModel) : null);
    }

    public static double Runtime(BaseItem item) => (item.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond;

    /// <summary>The title as the service knows it: provider ids only. Null for titles without a TMDb, IMDb or TVDb id (home videos).</summary>
    public static SharedTitle? TitleOf(Video item)
    {
        double runtime = Runtime(item);
        var video = item.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);
        double? fps = video?.RealFrameRate ?? video?.AverageFrameRate;
        string edition = ShareMapping.Edition(item.Path);
        return item switch
        {
            Movie m => new SharedTitle("movie", ShareMapping.Int(m.ProviderIds, "Tmdb"), ShareMapping.Str(m.ProviderIds, "Imdb"), null, null, null, null, edition, runtime, fps),
            Episode { Series: { } s, ParentIndexNumber: { } season, IndexNumber: { } number } e => new SharedTitle(
                "episode",
                null,
                null,
                new SharedSeries(ShareMapping.Int(s.ProviderIds, "Tmdb"), ShareMapping.Int(s.ProviderIds, "Tvdb")),
                season,
                number,
                ShareMapping.Int(e.ProviderIds, "Tmdb"),
                edition,
                runtime,
                fps),
            _ => null,
        };
    }

    /// <summary>
    /// Audio tracks to fingerprint, as indexes among the audio streams: the dialogue track first,
    /// then up to two others. A dub-first file then still aligns on its original-language track
    /// (phase 0 follow-up 1).
    /// </summary>
    public static IReadOnlyList<int> AudioTracks(BaseItem item)
    {
        var audio = item.GetMediaStreams().Where(s => s.Type == MediaStreamType.Audio).ToList();
        var main = MediaTracks.DialogueAudio(item);
        var order = audio.Select((s, i) => (S: s, I: i))
            .Where(x => !(x.S.Title ?? string.Empty).Contains("Commentary", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => main is not null && x.S.Index == main.Index ? 0 : 1)
            .ThenBy(x => x.I)
            .GroupBy(x => x.S.Language ?? x.I.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Select(g => g.First().I)
            .Take(3)
            .ToList();
        return order.Count > 0 ? order : [0];
    }

    private int? TmdbOf(Guid personId) => ShareMapping.Int(_library.GetItemById(personId)?.ProviderIds, "Tmdb");

    private string? MbidOf(Guid songId) => ShareMapping.Str(_library.GetItemById(songId)?.ProviderIds, "MusicBrainzRecording");

    private Guid? SongByMbid(string mbid)
    {
        var song = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Audio],
            Recursive = true,
            HasAnyProviderId = new Dictionary<string, string> { ["MusicBrainzRecording"] = mbid },
            Limit = 1,
        }).FirstOrDefault();
        return song?.Id;
    }

    /// <summary>Credited actors by TMDb id: an episode's guests plus the series regulars.</summary>
    public Dictionary<int, LocalPerson> CastByTmdb(BaseItem item)
    {
        var credits = new List<PersonInfo>(_library.GetPeople(item));
        if (item is Episode { Series: { } series })
        {
            credits.AddRange(_library.GetPeople(series));
        }

        var map = new Dictionary<int, LocalPerson>();
        foreach (var c in credits.Where(p => p.Type is PersonKind.Actor or PersonKind.GuestStar && !string.IsNullOrWhiteSpace(p.Name)))
        {
            var person = _library.GetPerson(c.Name);
            int? tmdb = ShareMapping.Int(person?.ProviderIds, "Tmdb") ?? ShareMapping.Int(c.ProviderIds, "Tmdb");
            Guid id = person?.Id ?? c.Id;
            if (tmdb is { } t && id != Guid.Empty)
            {
                map.TryAdd(t, new LocalPerson(id, c.Name, c.Role));
            }
        }

        return map;
    }
}
