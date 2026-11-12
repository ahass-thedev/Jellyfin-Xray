using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SceneXray.Analysis;
using Jellyfin.Plugin.SceneXray.Configuration;
using Jellyfin.Plugin.SceneXray.Music;
using Jellyfin.Plugin.SceneXray.Storage;
using Jellyfin.Plugin.SceneXray.Vision;
using Jellyfin.Plugin.SceneXray.Voice;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace Jellyfin.Plugin.SceneXray.Services;

/// <summary>
/// Runs analysis (several titles at once when the queue allows it) and owns the expensive shared
/// state: the ONNX engine (loaded on demand, shared by every run, unloaded after 10 idle minutes),
/// the face galleries and the music index.
/// </summary>
public sealed class AnalysisService : IDisposable
{
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(10);
    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _encoder;
    private readonly CastResolver _cast;
    private readonly MusicService _music;
    private readonly ILogger<AnalysisService> _log;
    private readonly ModelStore _models;
    private readonly SemaphoreSlim _modelLock = new(1, 1); // loading, swapping and unloading models
    private readonly Dictionary<IDisposable, int> _modelUsers = []; // model -> runs using it
    private readonly HashSet<IDisposable> _retired = []; // replaced models still in use by a run
    private int _active;
    private readonly Dictionary<string, Gallery> _galleries = [];
    private readonly Timer _idleTimer;
    private IFaceEngine? _engine;
    private SpeakerModel? _speaker;
    private Gallery? _voiceGallery;
    private ModelPack _enginePack;
    private int _engineThreads;
    private InferenceDevice _engineDevice;
    private int _speakerThreads;
    private InferenceDevice _speakerDevice;
    private DateTime _lastUse = DateTime.UtcNow;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTime ItemModified, DateTime Read, FileStamp File)> _stamps = new();
    private static readonly TimeSpan StampLifetime = TimeSpan.FromHours(6);

    public AnalysisService(
        ILibraryManager library,
        IMediaEncoder encoder,
        CastResolver cast,
        MusicService music,
        IHttpClientFactory http,
        ILogger<AnalysisService> log)
    {
        _library = library;
        _encoder = encoder;
        _cast = cast;
        _music = music;
        _log = log;
        string root = Plugin.Instance!.DataDirectory;
        _models = new ModelStore(Path.Combine(root, "models"), http, log);
        Accelerator = new Accelerator(Path.Combine(root, "runtimes"), http, log, Plugin.Instance.Configuration);
        Plugin.Instance.ConfigurationChanged += (_, c) =>
        {
            if (c is PluginConfiguration pc)
            {
                Accelerator.OnConfigurationChanged(pc);
            }
        };
        Store = new TimelineStore(Path.Combine(root, "timelines"));
        _idleTimer = new Timer(_ => UnloadIfIdle(), null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
    }

    public TimelineStore Store { get; }

    /// <summary>Gets what picks the CPU or a GPU for the face and voice models.</summary>
    public Accelerator Accelerator { get; }

    /// <summary>Gets what the face models run on, for the settings page.</summary>
    public string FaceDevice { get; private set; } = NotLoaded;

    /// <summary>Gets what the voice model runs on, for the settings page.</summary>
    public string VoiceDevice { get; private set; } = NotLoaded;

    private const string NotLoaded = "not used yet (it is set up when the next title is analysed)";

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>Movies and episodes backed by a real file.</summary>
    public static bool IsEligible(BaseItem? item) => IsVideoWithPath(item) && File.Exists(item!.Path);

    private static bool IsVideoWithPath(BaseItem? item) =>
        item is Movie or Episode && !item.IsVirtualItem && !string.IsNullOrEmpty(item.Path);

    public static string Signature(BaseItem item, PluginConfiguration cfg) => Signature(FileStamp.Read(item.Path), cfg);

    private static string Signature(FileStamp file, PluginConfiguration cfg)
    {
        return string.Join(
            ':',
            file.Length,
            file.Ticks,
            Plugin.AnalyzerVersion,
            cfg.ModelPack,
            cfg.SampleFps.ToString(CultureInfo.InvariantCulture));
    }

    private static bool IsAnimated(BaseItem item)
    {
        var genres = item.Genres.AsEnumerable();
        if (item is Episode { Series: { } series })
        {
            genres = genres.Concat(series.Genres);
        }

        return genres.Any(g => g.Contains("Animation", StringComparison.OrdinalIgnoreCase) || g.Contains("Anime", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Voices are re-run on their own when only this changes.</summary>
    public static string VoiceSignature(BaseItem item) => VoiceSignature(FileStamp.Read(item.Path));

    private static string VoiceSignature(FileStamp file) =>
        string.Join(':', file.Length, file.Ticks, "voice1", ModelStore.SpeakerModel, Config.VoicesEnabled ? "on" : "off");

    /// <summary>Whether the title has an up-to-date timeline (file facts from <see cref="Stamp"/>).</summary>
    public bool IsCurrent(BaseItem item) =>
        IsVideoWithPath(item) && Stamp(item) is { Exists: true } && Store.Head(item.Id) is { } t && IsCurrent(t, item);

    /// <summary>Whether a title's timeline matches its file and the current settings.</summary>
    public bool IsCurrent(TimelineHead t, BaseItem item)
    {
        var file = Stamp(item);
        return Share.ShareMapping.SharedSignatureMatches(t.Signature, file.Length, file.Ticks)
            || (t.Signature == Signature(file, Config) && t.VoiceSignature == VoiceSignature(file) && t.SceneSignature == SceneSignature(file));
    }

    /// <summary>A received timeline stays current for as long as its file is unchanged (design §5).</summary>
    public static bool IsSharedCurrent(XrayTimeline t, BaseItem item)
    {
        var file = FileStamp.Read(item.Path);
        return Share.ShareMapping.SharedSignatureMatches(t.Signature, file.Length, file.Ticks);
    }

    /// <summary>
    /// A title's file size and time, remembered. Media often sits on a network share where every
    /// look costs milliseconds (4 ms over SMB on orpheus), and checking a whole library looks at
    /// thousands of files. Jellyfin changes the item's DateModified when its scan sees the file
    /// change, which drops the remembered facts; they are re-read after six hours anyway.
    /// Analysis itself always reads the file afresh.
    /// </summary>
    private FileStamp Stamp(BaseItem item)
    {
        var now = DateTime.UtcNow;
        if (_stamps.TryGetValue(item.Id, out var s) && s.ItemModified == item.DateModified && now - s.Read < StampLifetime)
        {
            return s.File;
        }

        var file = FileStamp.Read(item.Path);
        _stamps[item.Id] = (item.DateModified, now, file);
        return file;
    }

    /// <summary>Whether the title is in one of the configured libraries (all of them when none are picked).</summary>
    public bool InScope(BaseItem item)
    {
        var ids = Config.LibraryIds;
        if (ids.Length == 0)
        {
            return true;
        }

        var wanted = ids.Select(Guid.Parse).ToHashSet();
        return _library.GetCollectionFolders(item).Any(f => wanted.Contains(f.Id));
    }

    /// <summary>Titles in the configured libraries that have no up-to-date timeline.</summary>
    public List<BaseItem> Pending() => Titles().Where(i => !IsCurrent(i)).ToList();

    /// <summary>Every analysable movie and episode in the configured libraries, newest first.</summary>
    public List<BaseItem> Titles()
    {
        var cfg = Config;
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true,
            IsVirtualItem = false,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)], // newest first
        };
        if (cfg.LibraryIds.Length > 0)
        {
            query.TopParentIds = cfg.LibraryIds.Select(Guid.Parse).ToArray();
        }

        return _library.GetItemList(query).Where(i => IsVideoWithPath(i) && Stamp(i).Exists).ToList();
    }

    public async Task<XrayTimeline> AnalyzeAsync(Guid itemId, IProgress<double>? progress, CancellationToken ct)
    {
        var item = _library.GetItemById(itemId) as Video;
        if (!IsEligible(item))
        {
            throw new InvalidOperationException("Not an analysable movie or episode: " + itemId);
        }

        if (!NativeLibraries.IsSupported)
        {
            throw new PlatformNotSupportedException(NativeLibraries.UnsupportedReason);
        }

        Interlocked.Increment(ref _active);
        var held = new List<IDisposable>();
        try
        {
            var cfg = Config;
            _lastUse = DateTime.UtcNow;
            double duration = (item!.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond;
            var previous = Store.Get(item.Id);
            bool doFaces = previous?.Signature != Signature(item, cfg);

            // Faces (with music) take most of the time; a voice-only pass is a few minutes.
            double faceShare = doFaces ? 85 : 0;
            XrayTimeline timeline;
            List<FrameColor>? frameColors = null;
            if (doFaces)
            {
                _log.LogInformation("X-Ray: analysing {Name}", item.Name);
                (timeline, frameColors) = await FacesAndMusicAsync(item, cfg, duration, previous, held, new Progress<double>(p => progress?.Report(p * faceShare / 100)), ct).ConfigureAwait(false);
            }
            else
            {
                _log.LogInformation("X-Ray: adding voices to {Name}", item.Name);
                timeline = previous!;
            }

            if (doFaces || timeline.VoiceSignature != VoiceSignature(item))
            {
                await VoicesAsync(item, timeline, cfg, duration, held, new Progress<double>(p => progress?.Report(faceShare + (p * (95 - faceShare)))), ct).ConfigureAwait(false);
            }

            if (doFaces || timeline.SceneSignature != SceneSignature(item))
            {
                await ScenesAsync(item, timeline, cfg, frameColors, ct).ConfigureAwait(false);
            }

            Store.Save(timeline);
            progress?.Report(100);
            _log.LogInformation(
                "X-Ray: {Name} done: {People} people by face, {Voices} by voice, {Unknown} unknown, {Music} songs",
                item.Name,
                timeline.People.Count,
                timeline.VoicePeople.Count,
                timeline.Unknown.Count,
                timeline.Music.Count);
            return timeline;
        }
        finally
        {
            await ReleaseModelsAsync(held).ConfigureAwait(false);
            _lastUse = DateTime.UtcNow;
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>The timeline, plus the face pass's frame colours for scene detection (null when it saw no frames).</summary>
    private async Task<(XrayTimeline Timeline, List<FrameColor>? FrameColors)> FacesAndMusicAsync(Video item, PluginConfiguration cfg, double duration, XrayTimeline? previous, List<IDisposable> held, IProgress<double> progress, CancellationToken ct)
    {
        bool skipFaces = cfg.SkipAnimation && IsAnimated(item);
        var engine = skipFaces ? null! : await EngineAsync(cfg, held, ct).ConfigureAwait(false);
        var gallery = GalleryFor(cfg.ModelPack);
        var cast = skipFaces ? [] : await _cast.ResolveAsync(item, engine, gallery, cfg, ct).ConfigureAwait(false);
        var options = new FaceAnalysisOptions(
            new DecodeOptions(_encoder.EncoderPath, _encoder.ProbePath, cfg.SampleFps, cfg.DecodeWidth, cfg.HardwareDecode, cfg.VaapiDevice),
            cfg.MinFaceHeight,
            (float)cfg.ClusterThreshold,
            (float)cfg.IdentifyThreshold,
            (float)cfg.IdentifyMargin,
            (float)cfg.WeakIdentifyThreshold,
            cfg.SceneGapSeconds,
            cfg.MinScreenTimeSeconds);

        // Cartoons have no faces to find; their cast comes from voices.
        var faces = skipFaces
            ? new FaceAnalysisResult()
            : await new FaceAnalyzer(engine).AnalyzeAsync(item.Path, cast, options, duration, new Progress<double>(p => progress.Report(p * 90)), ct).ConfigureAwait(false);

        List<MusicCue> music = [];
        bool musicDone = false;
        if (cfg.MusicEnabled)
        {
            try
            {
                music = await _music.IdentifyAsync(item, cfg, duration, ct).ConfigureAwait(false);
                musicDone = true;
            }
            catch (InvalidOperationException ex)
            {
                _log.LogWarning(ex, "X-Ray: music identification failed for {Name}", item.Name);
            }
        }

        if (cfg.LearnFromMatches)
        {
            foreach (var (person, embedding, score) in faces.Learned)
            {
                gallery.Learn(person, item.Id, embedding, score, manual: false);
            }
        }

        gallery.Save();
        progress.Report(100);
        var timeline = new XrayTimeline
        {
            ItemId = item.Id,
            AnalyzedAt = DateTime.UtcNow,
            DurationSeconds = duration,
            Signature = Signature(item, cfg),
            Analyzer = new AnalyzerInfo { Version = Plugin.AnalyzerVersion, ModelPack = cfg.ModelPack.ToString(), SampleFps = cfg.SampleFps },
            People = faces.People,
            Unknown = faces.Unknown,
            Music = music,
            MusicAnalyzed = musicDone,
            Stats = faces.Stats,
        };

        // Admin decisions survive re-analysis.
        if (previous is not null)
        {
            ReapplyManualEdits(previous, timeline, options);
        }

        return (timeline, faces.FrameColors.Count > 0 ? faces.FrameColors : null);
    }

    /// <summary>
    /// Finds who speaks when, and names the voices automatically (subtitle tags, the voice
    /// gallery, names in the dialogue, faces on screen). This is how cartoon voice casts and
    /// costumed or off-screen characters appear.
    /// </summary>
    private async Task VoicesAsync(Video item, XrayTimeline timeline, PluginConfiguration cfg, double duration, List<IDisposable> held, IProgress<double> progress, CancellationToken ct)
    {
        timeline.VoiceSignature = VoiceSignature(item);
        timeline.VoicePeople = [];
        if (!cfg.VoicesEnabled)
        {
            timeline.Voice = new VoiceStats { Note = "voices turned off" };
            return;
        }

        var sub = MediaTracks.DialogueSubtitles(item);
        var audio = MediaTracks.DialogueAudio(item);
        if (sub is null || audio is null)
        {
            timeline.Voice = new VoiceStats { Note = sub is null ? "no text subtitles" : "no audio track" };
            return;
        }

        List<SubtitleCue> cues;
        try
        {
            cues = Subtitles.Parse(await MediaTracks.ReadSrtAsync(item, sub, _encoder.EncoderPath, ct).ConfigureAwait(false));
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning(ex, "X-Ray: could not read subtitles of {Name}", item.Name);
            timeline.Voice = new VoiceStats { Note = "subtitles unreadable" };
            return;
        }

        if (cues.Count < 20)
        {
            timeline.Voice = new VoiceStats { Lines = cues.Count, Note = "too few subtitle lines" };
            return;
        }

        var speaker = await SpeakerAsync(cfg, held, ct).ConfigureAwait(false);
        var analysis = await VoiceAnalyzer.AnalyzeAsync(
            speaker,
            item.Path,
            cues,
            new VoiceOptions(_encoder.EncoderPath, audio.Index, (audio.Channels ?? 2) >= 6),
            duration,
            progress,
            ct).ConfigureAwait(false);

        var voiceGallery = VoiceGallery;
        var candidates = Credits(item)
            .Select(c => new VoiceCandidate
            {
                Character = new Character(c.PersonId, c.Name, c.Role ?? string.Empty, DialogueNamer.NamesFromRole(c.Role ?? string.Empty)),
                References = voiceGallery.References(c.PersonId, item.Id),
                FaceSpans = timeline.People.FirstOrDefault(p => p.PersonId == c.PersonId) is { } face ? face.Spans ?? face.Intervals : [],
            })
            .ToList();
        var labels = VoiceNamer.Name(analysis, candidates);

        foreach (var l in labels.Where(l => l.Learnable && l.Cluster.Cues.Count >= 10))
        {
            voiceGallery.Learn(l.Person.Character.PersonId, item.Id, l.Cluster.Centroid, l.Score, manual: false);
        }

        voiceGallery.Save();
        timeline.VoicePeople = labels
            .GroupBy(l => l.Person)
            .Where(g => !timeline.RejectedPersonIds.Contains(g.Key.Character.PersonId))
            .Select(g =>
            {
                var (seconds, intervals) = VoiceNamer.Spans(g.Select(l => l.Cluster), cfg.SceneGapSeconds);
                return new TimelinePerson
                {
                    PersonId = g.Key.Character.PersonId,
                    Name = g.Key.Character.ActorName,
                    Role = g.Key.Character.Role,
                    ScreenTimeSeconds = seconds,
                    Confidence = Math.Round(Math.Min(1, g.Max(l => l.Score)), 3),
                    Intervals = intervals,
                    Source = "voice:" + string.Join('+', g.Select(l => l.Source).Distinct()),
                };
            })
            .Where(p => p.ScreenTimeSeconds >= 2)
            .OrderByDescending(p => p.ScreenTimeSeconds)
            .ToList();
        timeline.Voice = new VoiceStats
        {
            Lines = cues.Count,
            Embedded = analysis.Embedded,
            Voices = analysis.Clusters.Count(c => c.Cues.Count >= 3),
            Named = timeline.VoicePeople.Count,
            Seconds = analysis.Seconds,
        };
    }

    /// <summary>
    /// Scene boundaries, from the face pass's frames when it just ran, otherwise from a light
    /// decode-only pass (cartoons, and titles analysed before scenes existed).
    /// </summary>
    private async Task ScenesAsync(Video item, XrayTimeline timeline, PluginConfiguration cfg, List<FrameColor>? colors, CancellationToken ct)
    {
        if (colors is null)
        {
            colors = [];
            var o = new DecodeOptions(_encoder.EncoderPath, _encoder.ProbePath, 1, 320, cfg.HardwareDecode, cfg.VaapiDevice);
            var (w, h, sar) = await FrameSource.ProbeAsync(o, item.Path, ct).ConfigureAwait(false);
            await foreach (var (_, time, frame) in FrameSource.ReadAsync(o, item.Path, w, h, sar, ct).ConfigureAwait(false))
            {
                colors.Add(new FrameColor(time, frame.ColorHistogram()));
            }
        }

        var presence = timeline.People.Concat(timeline.VoicePeople)
            .GroupBy(p => p.PersonId)
            .ToDictionary(g => g.Key, g => g.SelectMany(p => p.Spans ?? p.Intervals).ToList());
        timeline.Scenes = SceneDetector.Detect(colors, presence);
        timeline.SceneSignature = SceneSignature(item);
    }

    public static string SceneSignature(BaseItem item) => SceneSignature(FileStamp.Read(item.Path));

    private static string SceneSignature(FileStamp file) => string.Join(':', file.Length, file.Ticks, "scene1");

    /// <summary>Credited actors (an episode's guests plus the series regulars), one entry per person.</summary>
    private List<(Guid PersonId, string Name, string? Role)> Credits(BaseItem item)
    {
        var credits = new List<PersonInfo>(_library.GetPeople(item));
        if (item is Episode { Series: { } series })
        {
            credits.AddRange(_library.GetPeople(series));
        }

        return credits
            .Where(p => p.Type is PersonKind.Actor or PersonKind.GuestStar && !string.IsNullOrWhiteSpace(p.Name))
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(p => p.Type == PersonKind.GuestStar ? 0 : 1).First())
            .Select(p => (_library.GetPerson(p.Name)?.Id ?? p.Id, p.Name, (string?)p.Role))
            .Where(p => p.Item1 != Guid.Empty)
            .ToList();
    }

    /// <summary>
    /// Names an unknown face: it joins the cast and, when <paramref name="teach"/>, teaches the
    /// gallery. An admin's name has no <paramref name="source"/>; a name from the sharing service
    /// comes through the same path with source "sharing" and teaches only when at least two
    /// independent voices back it (design D15).
    /// </summary>
    public XrayTimeline? AssignUnknown(Guid itemId, int unknownId, Guid personId, string? source = null, double confidence = 1, bool teach = true)
    {
        var t = Store.Get(itemId);
        var unknown = t?.Unknown.FirstOrDefault(u => u.Id == unknownId);
        var person = _library.GetItemById(personId);
        if (t is null || unknown is null || person is null)
        {
            return null;
        }

        string? role = _library.GetPeople(_library.GetItemById(itemId)!)
            .FirstOrDefault(p => string.Equals(p.Name, person.Name, StringComparison.OrdinalIgnoreCase))?.Role;
        bool manual = source is null;
        MergeInto(t, personId, person.Name, role, unknown.Intervals, unknown.ScreenTimeSeconds, manual);
        if (!manual && t.People.First(p => p.PersonId == personId) is { Confidence: 0 } added)
        {
            added.Confidence = Math.Round(confidence, 3);
            added.Source = "face:" + source;
        }

        t.Unknown.Remove(unknown);
        t.ManualAssignments.RemoveAll(m => m.UnknownEmbedding == unknown.Embedding);
        t.ManualAssignments.Add(new ManualAssignment { PersonId = personId, UnknownEmbedding = unknown.Embedding, Source = source, Taught = teach });
        Store.Save(t);

        if (teach && EmbeddingCodec.Decode(unknown.Embedding) is { } e)
        {
            var gallery = GalleryFor(Config.ModelPack);
            gallery.Learn(personId, itemId, e, manual ? 1 : confidence, manual);
            gallery.Save();
        }

        return t;
    }

    /// <summary>A shared name that now has two voices behind it teaches the gallery after all.</summary>
    public bool TeachSharedName(Guid itemId, Guid personId)
    {
        var t = Store.Get(itemId);
        var m = t?.ManualAssignments.FirstOrDefault(x => x.PersonId == personId && x.Source == "sharing" && !x.Taught);
        if (t is null || m is null || EmbeddingCodec.Decode(m.UnknownEmbedding) is not { } e)
        {
            return false;
        }

        m.Taught = true;
        Store.Save(t);
        var gallery = GalleryFor(Config.ModelPack);
        gallery.Learn(personId, itemId, e, t.People.FirstOrDefault(p => p.PersonId == personId)?.Confidence ?? 0.7, manual: false);
        gallery.Save();
        return true;
    }

    /// <summary>An admin says a name is wrong: drop it here and forget what this title taught.</summary>
    public XrayTimeline? RemovePerson(Guid itemId, Guid personId)
    {
        var t = Store.Get(itemId);
        if (t is null || t.People.RemoveAll(p => p.PersonId == personId) + t.VoicePeople.RemoveAll(p => p.PersonId == personId) == 0)
        {
            return t;
        }

        if (!t.RejectedPersonIds.Contains(personId))
        {
            t.RejectedPersonIds.Add(personId);
        }

        t.ManualAssignments.RemoveAll(m => m.PersonId == personId);
        Store.Save(t);
        var gallery = GalleryFor(Config.ModelPack);
        gallery.Forget(personId, itemId);
        gallery.Save();
        return t;
    }

    private static void ReapplyManualEdits(XrayTimeline previous, XrayTimeline next, FaceAnalysisOptions o)
    {
        next.RejectedPersonIds = previous.RejectedPersonIds;
        next.People.RemoveAll(p => next.RejectedPersonIds.Contains(p.PersonId));
        next.ManualAssignments = previous.ManualAssignments;
        foreach (var m in previous.ManualAssignments)
        {
            // Find the new unknown cluster that is the same face as the one the admin named.
            var e = EmbeddingCodec.Decode(m.UnknownEmbedding);
            var match = e is null ? null : next.Unknown
                .Select(u => (U: u, S: EmbeddingCodec.Decode(u.Embedding) is { } ue ? Vec.Dot(e, ue) : -1))
                .Where(x => x.S >= o.ClusterThreshold)
                .OrderByDescending(x => x.S)
                .Select(x => x.U)
                .FirstOrDefault();
            if (match is null)
            {
                continue;
            }

            var name = previous.People.FirstOrDefault(p => p.PersonId == m.PersonId);
            MergeInto(next, m.PersonId, name?.Name ?? string.Empty, name?.Role, match.Intervals, match.ScreenTimeSeconds, manual: m.Source is null);
            next.Unknown.Remove(match);
        }
    }

    private static void MergeInto(XrayTimeline t, Guid personId, string name, string? role, List<double[]> intervals, double screen, bool manual)
    {
        var p = t.People.FirstOrDefault(x => x.PersonId == personId);
        if (p is null)
        {
            p = new TimelinePerson { PersonId = personId, Name = name, Role = role, Confidence = manual ? 1 : 0 };
            t.People.Add(p);
        }

        p.ScreenTimeSeconds = Math.Round(p.ScreenTimeSeconds + screen, 1);
        p.Intervals = p.Intervals.Concat(intervals).OrderBy(i => i[0])
            .Aggregate(new List<double[]>(), (acc, i) =>
            {
                if (acc.Count > 0 && i[0] <= acc[^1][1])
                {
                    acc[^1][1] = Math.Max(acc[^1][1], i[1]);
                }
                else
                {
                    acc.Add([i[0], i[1]]);
                }

                return acc;
            });
        t.People.Sort((a, b) => b.ScreenTimeSeconds.CompareTo(a.ScreenTimeSeconds));
    }

    /// <summary>The face engine for these settings, held for the run until <see cref="ReleaseModelsAsync"/>.</summary>
    private async Task<IFaceEngine> EngineAsync(PluginConfiguration cfg, List<IDisposable> held, CancellationToken ct)
    {
        await _modelLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Hold(await LoadEngineAsync(cfg, ct).ConfigureAwait(false), held);
        }
        finally
        {
            _modelLock.Release();
        }
    }

    private async Task<IFaceEngine> LoadEngineAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        if (_engine is not null && _enginePack == cfg.ModelPack && _engineThreads == cfg.InferenceThreads && _engineDevice == cfg.InferenceDevice)
        {
            return _engine;
        }

        Retire(_engine);
        _engine = null;
        NativeLibraries.Register();
        var (det, rec) = await _models.EnsureAsync(cfg.ModelPack, ct).ConfigureAwait(false);
        var backend = await Accelerator.ResolveAsync(cfg.InferenceDevice, cfg.InferenceThreads, ct).ConfigureAwait(false);
        try
        {
            _engine = Accelerator.Trial(backend, () => Warm(new OnnxFaceEngine(det, rec, backend)));
        }
        catch (Exception ex) when (backend.IsGpu && ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException)
        {
            backend = Accelerator.Fail(ex.Message, cfg.InferenceThreads);
            _engine = new OnnxFaceEngine(det, rec, backend);
        }

        FaceDevice = backend.Name;
        _log.LogInformation("X-Ray: face models on {Backend}", FaceDevice);
        _enginePack = cfg.ModelPack;
        _engineThreads = cfg.InferenceThreads;
        _engineDevice = cfg.InferenceDevice;
        return _engine;
    }

    /// <summary>One detection and one embedding, so a GPU compiles its kernels (and shows it works) before a title starts.</summary>
    private static OnnxFaceEngine Warm(OnnxFaceEngine engine)
    {
        try
        {
            engine.Detect(new RgbImage(1280, 720));
            engine.Embed([new RgbImage(FaceAligner.Size, FaceAligner.Size)]);
            return engine;
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }

    private Gallery VoiceGallery
    {
        get
        {
            lock (_galleries)
            {
                return _voiceGallery ??= new Gallery(Path.Combine(Plugin.Instance!.DataDirectory, "gallery", "voice-" + Path.GetFileNameWithoutExtension(ModelStore.SpeakerModel) + ".json"));
            }
        }
    }

    // Model bookkeeping, all under _modelLock. A run holds the models it took until it ends, so
    // a settings change (which loads new ones for the next run) never disposes one in use.
    private T Hold<T>(T model, List<IDisposable> held)
        where T : IDisposable
    {
        _modelUsers[model] = _modelUsers.GetValueOrDefault(model) + 1;
        held.Add(model);
        return model;
    }

    private void Retire(IDisposable? model)
    {
        if (model is null)
        {
            return;
        }

        if (_modelUsers.ContainsKey(model))
        {
            _retired.Add(model); // disposed when its last run lets go
        }
        else
        {
            model.Dispose();
        }
    }

    private async Task ReleaseModelsAsync(List<IDisposable> held)
    {
        if (held.Count == 0)
        {
            return;
        }

        await _modelLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var model in held)
            {
                if (--_modelUsers[model] > 0)
                {
                    continue;
                }

                _modelUsers.Remove(model);
                if (_retired.Remove(model))
                {
                    model.Dispose();
                }
            }
        }
        finally
        {
            _modelLock.Release();
        }
    }

    /// <summary>The voice model for these settings, held for the run until <see cref="ReleaseModelsAsync"/>.</summary>
    private async Task<SpeakerModel> SpeakerAsync(PluginConfiguration cfg, List<IDisposable> held, CancellationToken ct)
    {
        await _modelLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return Hold(await LoadSpeakerAsync(cfg, ct).ConfigureAwait(false), held);
        }
        finally
        {
            _modelLock.Release();
        }
    }

    private async Task<SpeakerModel> LoadSpeakerAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        if (_speaker is not null && _speakerThreads == cfg.InferenceThreads && _speakerDevice == cfg.VoiceDevice)
        {
            return _speaker;
        }

        Retire(_speaker);
        _speaker = null;
        NativeLibraries.Register();
        string path = await _models.EnsureSpeakerModelAsync(ct).ConfigureAwait(false);
        var backend = await Accelerator.ResolveAsync(cfg.VoiceDevice, cfg.InferenceThreads, ct).ConfigureAwait(false);
        try
        {
            _speaker = Accelerator.Trial(backend, () => Warm(new SpeakerModel(path, backend)));
        }
        catch (Exception ex) when (backend.IsGpu && ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException)
        {
            backend = Accelerator.Fail(ex.Message, cfg.InferenceThreads);
            _speaker = new SpeakerModel(path, backend);
        }

        VoiceDevice = backend.Name;
        _log.LogInformation("X-Ray: voice model on {Backend}", VoiceDevice);
        _speakerThreads = cfg.InferenceThreads;
        _speakerDevice = cfg.VoiceDevice;
        return _speaker;
    }

    /// <summary>One voiceprint, so a GPU compiles its kernels (and shows it works) before a title starts.</summary>
    private static SpeakerModel Warm(SpeakerModel model)
    {
        try
        {
            model.Warm();
            return model;
        }
        catch
        {
            model.Dispose();
            throw;
        }
    }

    /// <summary>Embeddings depend only on the recognizer, so packs sharing one share a gallery.</summary>
    private Gallery GalleryFor(ModelPack pack)
    {
        string recognizer = Path.GetFileNameWithoutExtension(ModelStore.FilesFor(pack).Recognizer);
        lock (_galleries)
        {
            if (!_galleries.TryGetValue(recognizer, out var g))
            {
                g = new Gallery(Path.Combine(Plugin.Instance!.DataDirectory, "gallery", recognizer + ".json"));
                _galleries[recognizer] = g;
            }

            return g;
        }
    }

    private void UnloadIfIdle()
    {
        if (DateTime.UtcNow - _lastUse < IdleUnload || Volatile.Read(ref _active) > 0 || !_modelLock.Wait(0))
        {
            return;
        }

        try
        {
            // A run that starts now waits on _modelLock for its models and loads them afresh.
            if (_modelUsers.Count == 0 && (_engine is not null || _speaker is not null))
            {
                _engine?.Dispose();
                _engine = null;
                _speaker?.Dispose();
                _speaker = null;
                _music.ReleaseIndex();
                _log.LogDebug("X-Ray: unloaded face models after idling");
            }
        }
        finally
        {
            _modelLock.Release();
        }
    }

    public void Dispose()
    {
        _idleTimer.Dispose();
        _engine?.Dispose();
        _speaker?.Dispose();
        foreach (var model in _retired)
        {
            model.Dispose();
        }

        _modelLock.Dispose();
    }
}

/// <summary>A media file's size and last write time (UTC ticks), the parts of every signature that come from the file.</summary>
public readonly record struct FileStamp(bool Exists, long Length, long Ticks)
{
    public static FileStamp Read(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? new FileStamp(true, info.Length, info.LastWriteTimeUtc.Ticks) : default;
    }
}
