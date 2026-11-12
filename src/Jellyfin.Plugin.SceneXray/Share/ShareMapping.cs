using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SceneXray.Analysis;
using Jellyfin.Plugin.SceneXray.Sharing;
using Jellyfin.Plugin.SceneXray.Storage;

namespace Jellyfin.Plugin.SceneXray.Share;

/// <summary>A local person a received TMDb id maps to.</summary>
public sealed record LocalPerson(Guid PersonId, string Name, string? Role);

/// <summary>
/// Conversions between the local timeline and the wire formats (design §2). Pure: everything
/// Jellyfin-specific (provider ids, people) is passed in, so this is what the tests pin.
/// </summary>
public static partial class ShareMapping
{
    /// <summary>
    /// The edition label from a file name: Jellyfin's <c>{edition-…}</c> tag, else the version
    /// suffix after " - ". Only the normalised label leaves the server, never the name.
    /// </summary>
    public static string Edition(string? path)
    {
        string name = Path.GetFileNameWithoutExtension(path ?? string.Empty);
        var tag = EditionTag().Match(name);
        if (tag.Success)
        {
            return TitleKey.Edition(tag.Groups[1].Value);
        }

        int dash = name.LastIndexOf(" - ", StringComparison.Ordinal);
        return TitleKey.Edition(dash > 0 ? name[(dash + 3)..] : null);
    }

    public static int? Int(IReadOnlyDictionary<string, string>? ids, string key) =>
        ids is not null && ids.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i > 0 ? i : null;

    public static string? Str(IReadOnlyDictionary<string, string>? ids, string key) =>
        ids is not null && ids.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    /// <summary>
    /// Builds the contribution field by field from the timeline (never by trimming it).
    /// <paramref name="tmdbOf"/> maps a Jellyfin person to its TMDb id; people without one are not
    /// shared. Unknown faces are added separately (phase 2b).
    /// </summary>
    public static SharedContribution Contribution(
        XrayTimeline t,
        SharedTitle title,
        SharedAnalyzer analyzer,
        AnchorSet anchors,
        Func<Guid, int?> tmdbOf,
        Func<Guid, string?> mbidOf,
        IReadOnlyList<SharedUnknownFace>? unknownFaces = null)
    {
        double runtime = title.Runtime;
        List<double[]> Clean(IEnumerable<double[]> intervals) =>
            TimelineMapping.Clip(TimelineMapping.Merge(intervals.Select(i => new[] { Math.Round(i[0], 2), Math.Round(i[1], 2) })), 0, runtime);

        List<SharedPerson> People(IEnumerable<TimelinePerson> people, string source) => people
            .Select(p => (Tmdb: tmdbOf(p.PersonId), P: p))
            .Where(x => x.Tmdb is not null && !t.RejectedPersonIds.Contains(x.P.PersonId))
            .GroupBy(x => x.Tmdb!.Value)
            .Select(g => new SharedPerson(
                g.Key,
                source,
                Math.Round(Math.Clamp(g.Max(x => x.P.Confidence), 0, 1), 3),
                Clean(g.SelectMany(x => x.P.Intervals))))
            .Where(p => p.Intervals.Count > 0)
            .ToList();

        var scenes = TimelineMapping.Clip(t.Scenes.Where(s => s.Length >= 2).OrderBy(s => s[0]), 0, runtime);
        var music = t.Music
            .Where(m => m.End > m.Start && !string.IsNullOrWhiteSpace(m.Title))
            .Select(m => new SharedMusicCue(
                Math.Round(Math.Max(0, m.Start), 1),
                Math.Round(Math.Min(runtime, m.End), 1),
                Limit(m.Title)!,
                Limit(m.Artist),
                Limit(m.Album),
                m.ItemId is { } id ? mbidOf(id) : null,
                m.Source == "audd" ? "audd" : "library",
                Math.Round(Math.Clamp(m.Confidence, 0, 1), 3)))
            .Where(m => m.End > m.Start)
            .ToList();

        return new SharedContribution(
            title,
            analyzer,
            SharedAnchors.From(anchors),
            scenes,
            People(t.People, "face"),
            People(t.VoicePeople, "voice"),
            unknownFaces ?? [],
            music);
    }

    /// <summary>
    /// The unknown faces to share (design §2): the clusters the analyser already keeps as unknown
    /// (recurring, above the minimum screen time), as their mean embedding in float16. Never a
    /// thumbnail or face box. <paramref name="recognizer"/> is the model that made the embeddings.
    /// </summary>
    public static List<SharedUnknownFace> UnknownFaces(XrayTimeline t, string recognizer, double runtime)
    {
        if (!FaceSignature.Recognizers.Contains(recognizer))
        {
            return [];
        }

        return t.Unknown
            .Select(u => (U: u, Signature: FaceSignature.Encode(EmbeddingCodec.Decode(u.Embedding)), Intervals: TimelineMapping.Clip(TimelineMapping.Merge(u.Intervals), 0, runtime)))
            .Where(x => x.Signature is not null && x.Intervals.Count > 0 && x.U.ScreenTimeSeconds > 0)
            .Select(x => new SharedUnknownFace(x.U.Id, recognizer, x.Signature!, Math.Round(Math.Min(runtime, x.U.ScreenTimeSeconds), 1), x.Intervals))
            .Take(100)
            .ToList();
    }

    private static string? Limit(string? s) => s is null ? null : new string(s.Where(c => !char.IsControl(c)).Take(200).ToArray());

    /// <summary>
    /// A received consensus as a local timeline, mapped into this file's time through
    /// <paramref name="fit"/>. People the local library doesn't know are dropped; people an admin
    /// removed from this title stay removed.
    /// </summary>
    public static XrayTimeline Timeline(
        ConsensusTimeline c,
        SharedInfo fit,
        Guid itemId,
        double localRuntime,
        string signature,
        IReadOnlyDictionary<int, LocalPerson> cast,
        Func<string, Guid?> songByMbid,
        XrayTimeline? previous)
    {
        List<double[]> Map(IEnumerable<double[]> intervals)
        {
            if (fit.Segments is not { Count: > 0 } segments)
            {
                return TimelineMapping.ToLocal(intervals, fit.Slope, fit.Offset, fit.ValidFrom, fit.ValidTo, localRuntime);
            }

            var list = intervals.ToList();
            return TimelineMapping.Merge(segments.SelectMany(seg => TimelineMapping.ToLocal(list, fit.Slope, seg[2], seg[0], seg[1], localRuntime)));
        }

        var rejected = previous?.RejectedPersonIds ?? [];
        List<TimelinePerson> People(IEnumerable<ConsensusPerson> people, string source) => people
            .Where(p => cast.ContainsKey(p.TmdbPerson))
            .Select(p => (Local: cast[p.TmdbPerson], P: p, Intervals: Map(p.Intervals)))
            .Where(x => x.Intervals.Count > 0 && !rejected.Contains(x.Local.PersonId))
            .Select(x => new TimelinePerson
            {
                PersonId = x.Local.PersonId,
                Name = x.Local.Name,
                Role = x.Local.Role,
                Confidence = x.P.Confidence,
                Intervals = x.Intervals,
                ScreenTimeSeconds = Math.Round(TimelineMapping.Total(x.Intervals), 1),
                Source = source,
            })
            .OrderByDescending(p => p.ScreenTimeSeconds)
            .ToList();

        var music = c.Music
            .Select(m => (Cue: m, Span: Map([[m.Start, m.End]])))
            .Where(x => x.Span.Count == 1)
            .Select(x => new MusicCue
            {
                Start = x.Span[0][0],
                End = x.Span[0][1],
                Title = x.Cue.Title,
                Artist = x.Cue.Artist,
                Album = x.Cue.Album,
                ItemId = x.Cue.Mbid is { } mbid ? songByMbid(mbid) : null,
                Source = "shared",
                Confidence = x.Cue.Confidence,
            })
            .ToList();

        return new XrayTimeline
        {
            ItemId = itemId,
            AnalyzedAt = DateTime.UtcNow,
            DurationSeconds = localRuntime,
            Signature = signature,
            Analyzer = new AnalyzerInfo { Version = "shared:" + c.Version.ToString(CultureInfo.InvariantCulture), ModelPack = "shared" },
            People = People(c.People, "face"),
            VoicePeople = People(c.VoicePeople, "voice"),
            Scenes = Map(c.Scenes),
            Music = music,
            MusicAnalyzed = true,
            RejectedPersonIds = rejected,
            ManualAssignments = previous?.ManualAssignments ?? [],
            Shared = fit,
        };
    }

    /// <summary>The signature of a received timeline: current for as long as the file is unchanged.</summary>
    public static string SharedSignature(Guid variantId, long revision, long fileSize, long mtimeTicks) =>
        string.Create(CultureInfo.InvariantCulture, $"shared:{variantId:N}:{revision}:{fileSize}:{mtimeTicks}");

    /// <summary>A partial timeline (another cut, mapped piecewise): never current, so the title is still analysed here.</summary>
    public static string PartialSignature(Guid variantId, long revision, long fileSize, long mtimeTicks) =>
        string.Create(CultureInfo.InvariantCulture, $"sharedpart:{variantId:N}:{revision}:{fileSize}:{mtimeTicks}");

    public static bool IsShared(string? signature) =>
        signature?.StartsWith("shared:", StringComparison.Ordinal) == true || signature?.StartsWith("sharedpart:", StringComparison.Ordinal) == true;

    /// <summary>Whether a shared signature still belongs to the file (same size and mtime).</summary>
    public static bool SharedSignatureMatches(string? signature, long fileSize, long mtimeTicks) =>
        signature?.StartsWith("shared:", StringComparison.Ordinal) == true && signature.EndsWith(string.Create(CultureInfo.InvariantCulture, $":{fileSize}:{mtimeTicks}"), StringComparison.Ordinal);

    [GeneratedRegex(@"\{edition-([^}]+)\}", RegexOptions.IgnoreCase)]
    private static partial Regex EditionTag();
}
