using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SceneXray.Sharing;

namespace Xray.Share.Server;

/// <summary>
/// Structural checks (design §4) that run before anything is stored. A failure is a rejection with
/// a reason; no human sees it. The TMDb credits check is separate (<see cref="ICreditSource"/>),
/// because it needs the network.
/// </summary>
public static partial class Validation
{
    public const int MaxPeople = 300;
    public const int MaxScenes = 2000;
    public const int MaxMusic = 500;
    public const int MaxIntervalsPerPerson = 2000;
    public const int MaxString = 200;
    public const double MinRuntime = 60;
    public const double MaxRuntime = 6 * 3600;

    /// <summary>The analyser's major version: "2" and "2.1" are 2; anything else (including "shared:2") is null.</summary>
    public static int? MajorVersion(string? version)
    {
        var m = LeadingNumber().Match(version ?? string.Empty);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : null;
    }

    /// <summary>Null when the contribution is well formed; otherwise why not.</summary>
    public static string? Check(SharedContribution c, int minAnalyzerVersion)
    {
        if (c.Title is null || c.Analyzer is null || c.Anchors is null || c.Scenes is null || c.People is null
            || c.VoicePeople is null || c.UnknownFaces is null || c.Music is null)
        {
            return "missing fields";
        }

        var t = c.Title;
        if (t.Kind is not ("movie" or "episode"))
        {
            return "title kind must be movie or episode";
        }

        if (TitleKey.Of(t) is null)
        {
            return "title has no usable TMDb, IMDb or TVDb id";
        }

        if (!TitleKey.Editions.Contains(t.Edition))
        {
            return "unknown edition";
        }

        if (!double.IsFinite(t.Runtime) || t.Runtime < MinRuntime || t.Runtime > MaxRuntime)
        {
            return "runtime out of range";
        }

        if (t.FrameRate is { } fps && (!double.IsFinite(fps) || fps < 10 || fps > 121))
        {
            return "frame rate out of range";
        }

        if (MajorVersion(c.Analyzer.Version) is not { } major)
        {
            return "analyzer version is not a number";
        }

        if (major < minAnalyzerVersion)
        {
            return $"analyzer version {major} is older than the minimum {minAnalyzerVersion}";
        }

        if (Text(c.Analyzer.Plugin, 32) is null || Text(c.Analyzer.ModelPack, 32) is null)
        {
            return "analyzer fields invalid";
        }

        if (AnchorProblem(c.Anchors, t.Runtime) is { } anchors)
        {
            return anchors;
        }

        double runtime = t.Runtime;
        if (c.Scenes.Count > MaxScenes)
        {
            return "too many scenes";
        }

        if (IntervalProblem(c.Scenes, runtime, allowTouching: true) is { } scenes)
        {
            return "scenes: " + scenes;
        }

        if (c.Scenes.Count > 0 && c.Scenes.Average(s => s[1] - s[0]) < 1)
        {
            return "scenes: implausibly short";
        }

        if (c.People.Count + c.VoicePeople.Count > MaxPeople)
        {
            return "too many people";
        }

        foreach (var (list, kind) in new[] { (c.People, "face"), (c.VoicePeople, "voice") })
        {
            if (list.Select(p => p.TmdbPerson).Distinct().Count() != list.Count)
            {
                return $"{kind} people: a person is listed twice";
            }

            foreach (var p in list)
            {
                if (p.TmdbPerson <= 0 || p.Source != kind || !double.IsFinite(p.Confidence) || p.Confidence < 0 || p.Confidence > 1)
                {
                    return $"{kind} people: invalid person";
                }

                if (p.Intervals is null || p.Intervals.Count == 0 || p.Intervals.Count > MaxIntervalsPerPerson)
                {
                    return $"{kind} people: invalid intervals";
                }

                if (IntervalProblem(p.Intervals, runtime, allowTouching: false) is { } why)
                {
                    return $"{kind} people: {why}";
                }

                if (TimelineMapping.Total(p.Intervals) > runtime)
                {
                    return $"{kind} people: screen time above runtime";
                }
            }
        }

        if (c.Music.Count > MaxMusic)
        {
            return "too many music cues";
        }

        foreach (var m in c.Music)
        {
            if (!double.IsFinite(m.Start) || !double.IsFinite(m.End) || m.Start < 0 || m.End <= m.Start || m.End > runtime + 1)
            {
                return "music: cue outside the runtime";
            }

            if (Text(m.Title, MaxString) is not { Length: > 0 } || (m.Artist is not null && Text(m.Artist, MaxString) is null)
                || (m.Album is not null && Text(m.Album, MaxString) is null) || Text(m.Source, 16) is null
                || !double.IsFinite(m.Confidence) || m.Confidence < 0 || m.Confidence > 1)
            {
                return "music: invalid text";
            }

            if (m.Mbid is not null && !Guid.TryParseExact(m.Mbid, "D", out _))
            {
                return "music: invalid MusicBrainz id";
            }
        }

        return UnknownFacesProblem(c);
    }

    public const int MaxUnknownFaces = 100;

    /// <summary>
    /// Unknown faces (design §2): recurring faces only, each a 512-d float16 signature from a known
    /// recognizer, with sane intervals and screen time.
    /// </summary>
    public static string? UnknownFacesProblem(SharedContribution c)
    {
        if (c.UnknownFaces.Count > MaxUnknownFaces)
        {
            return "too many unknown faces";
        }

        if (c.UnknownFaces.Select(u => u.Id).Distinct().Count() != c.UnknownFaces.Count)
        {
            return "unknown faces: an id is listed twice";
        }

        foreach (var u in c.UnknownFaces)
        {
            if (u.Id < 0 || u.Recognizer is null || !FaceSignature.Recognizers.Contains(u.Recognizer) || FaceSignature.Decode(u.Embedding) is null)
            {
                return "unknown faces: invalid id, recognizer or signature";
            }

            if (!double.IsFinite(u.ScreenTime) || u.ScreenTime <= 0 || u.ScreenTime > c.Title.Runtime || u.Intervals is null || u.Intervals.Count == 0)
            {
                return "unknown faces: invalid screen time";
            }

            if (IntervalProblem(u.Intervals, c.Title.Runtime, allowTouching: false) is { } why)
            {
                return "unknown faces: " + why;
            }
        }

        return null;
    }

    public static string? AnchorProblem(SharedAnchors a, double runtime)
    {
        if (a.Length != AnchorSet.DefaultLength || a.Every != AnchorSet.EverySecondsFor(runtime))
        {
            return "anchors: unexpected spacing or length";
        }

        AnchorSet set;
        try
        {
            set = a.ToSet();
        }
        catch (FormatException)
        {
            return "anchors: not valid base64 windows";
        }

        // One window every `every` seconds over the whole runtime: a runtime claimed longer than
        // the audio is caught here.
        int expected = (int)Math.Floor(((runtime / FingerprintMatcher.ItemSeconds) - a.Length) * FingerprintMatcher.ItemSeconds / a.Every) + 1;
        if (Math.Abs(set.Count - expected) > 3)
        {
            return $"anchors: {set.Count} windows for a {runtime:F0} s runtime (expected about {expected})";
        }

        return set.Windows.Count(w => AnchorSet.IsInformative(w)) < Math.Min(20, set.Count / 4) ? "anchors: too little audio information" : null;
    }

    /// <summary>Sorted, finite, non-overlapping (or only touching, for scenes) and inside the runtime.</summary>
    public static string? IntervalProblem(IReadOnlyList<double[]> intervals, double runtime, bool allowTouching)
    {
        double last = double.NegativeInfinity;
        foreach (var i in intervals)
        {
            if (i is null || i.Length != 2 || !double.IsFinite(i[0]) || !double.IsFinite(i[1]))
            {
                return "malformed interval";
            }

            if (i[0] < 0 || i[1] > runtime + 1 || i[1] <= i[0])
            {
                return "interval outside the runtime";
            }

            if (allowTouching ? i[0] < last - 0.01 : i[0] < last)
            {
                return "intervals unsorted or overlapping";
            }

            last = i[1];
        }

        return null;
    }

    /// <summary>The string with control characters stripped, or null when longer than <paramref name="max"/>.</summary>
    public static string? Text(string? s, int max)
    {
        if (s is null)
        {
            return null;
        }

        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            if (!char.IsControl(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.Length > max ? null : sb.ToString();
    }

    /// <summary>The contribution with every free-text field stripped of control characters.</summary>
    public static SharedContribution Sanitized(SharedContribution c) => c with
    {
        Music = c.Music.Select(m => m with
        {
            Title = Text(m.Title, MaxString)!,
            Artist = Text(m.Artist, MaxString),
            Album = Text(m.Album, MaxString),
        }).ToList(),
    };

    [GeneratedRegex(@"^([0-9]{1,4})(\.[0-9]+)*$")]
    private static partial Regex LeadingNumber();
}
