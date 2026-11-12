using System.Diagnostics;
using Jellyfin.Plugin.SceneXray.Storage;
using Jellyfin.Plugin.SceneXray.Vision;

namespace Jellyfin.Plugin.SceneXray.Analysis;

/// <summary>Tuning for one face analysis run.</summary>
public sealed record FaceAnalysisOptions(
    DecodeOptions Decode,
    int MinFaceHeight,
    float ClusterThreshold,
    float IdentifyThreshold,
    float IdentifyMargin,
    float WeakIdentifyThreshold,
    double SceneGapSeconds,
    double MinScreenTimeSeconds);

/// <summary>Result of one face analysis run.</summary>
public sealed class FaceAnalysisResult
{
    public List<TimelinePerson> People { get; init; } = [];

    public List<UnknownFace> Unknown { get; init; } = [];

    /// <summary>Gets each sampled frame's colour fingerprint, for scene detection.</summary>
    public List<FrameColor> FrameColors { get; init; } = [];

    /// <summary>Gets cluster centroids confident enough to become reference faces later.</summary>
    public List<(Guid PersonId, float[] Embedding, float Score)> Learned { get; init; } = [];

    public AnalysisStats Stats { get; init; } = new();

    /// <summary>Gets one line per cluster (screen time, best and runner-up match) for tuning.</summary>
    public List<string> Diagnostics { get; init; } = [];
}

/// <summary>
/// Whole-title face pipeline: sample frames, detect, track within shots, embed a few good
/// crops per track, cluster tracks into people, then name each person from the cast.
/// </summary>
public sealed class FaceAnalyzer
{
    private const float ShotCutDistance = 0.45f;
    private readonly IFaceEngine _engine;

    public FaceAnalyzer(IFaceEngine engine) => _engine = engine;

    public async Task<FaceAnalysisResult> AnalyzeAsync(
        string path,
        IReadOnlyList<CastCandidate> cast,
        FaceAnalysisOptions o,
        double durationSeconds,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (srcW, srcH, sar) = await FrameSource.ProbeAsync(o.Decode, path, ct).ConfigureAwait(false);
        var tracker = new FaceTracker();
        var finished = new List<FaceTrack>();
        var pending = new List<FaceTrack>();
        float[]? prevHist = null;
        int frames = 0, faces = 0, frameW = 1, frameH = 1;
        double expected = Math.Max(1, durationSeconds * o.Decode.Fps);

        var colors = new List<FrameColor>();
        var tDetect = new Stopwatch();
        var tAlign = new Stopwatch();
        var tEmbed = new Stopwatch();
        await foreach (var (index, time, frame) in FrameSource.ReadAsync(o.Decode, path, srcW, srcH, sar, ct).ConfigureAwait(false))
        {
            frames++;
            (frameW, frameH) = (frame.Width, frame.Height);
            colors.Add(new FrameColor(time, frame.ColorHistogram()));
            var hist = frame.LumaHistogram();
            bool cut = prevHist is not null && RgbImage.HistogramDistance(prevHist, hist) > ShotCutDistance;
            prevHist = hist;

            var obs = new List<FaceObservation>();
            tDetect.Start();
            var boxes = _engine.Detect(frame, 0.5f);
            tDetect.Stop();
            tAlign.Start();
            foreach (var box in boxes)
            {
                if (box.Height < o.MinFaceHeight || box.Score < 0.6f)
                {
                    continue;
                }

                faces++;
                obs.Add(Observe(frame, index, time, box));
            }

            tAlign.Stop();

            tracker.Push(index, cut, obs);
            pending.AddRange(tracker.DrainClosed());
            if (pending.Sum(t => t.Best.Count) >= 32)
            {
                tEmbed.Start();
                EmbedTracks(pending);
                tEmbed.Stop();
                finished.AddRange(pending);
                pending.Clear();
            }

            if (frames % 50 == 0)
            {
                progress?.Report(Math.Min(0.95, frames / expected));
            }
        }

        tracker.CloseAll();
        pending.AddRange(tracker.DrainClosed());
        EmbedTracks(pending);
        finished.AddRange(pending);

        double frameSeconds = 1.0 / o.Decode.Fps;
        var usable = finished.Where(t => t.Embedding is not null).ToList();
        var clusters = TrackClusterer.Cluster(usable, o.ClusterThreshold);
        var matches = CastMatcher.Match(clusters, cast, frameSeconds, o.IdentifyThreshold, o.IdentifyMargin, o.WeakIdentifyThreshold);
        var people = TimelineBuilder.Build(matches, frameSeconds, o.SceneGapSeconds, o.MinScreenTimeSeconds);
        progress?.Report(1);

        var namedClusters = matches.Select(m => m.Cluster).ToHashSet();
        var unknown = clusters
            .Where(c => !namedClusters.Contains(c) && c.ScreenTime(frameSeconds) >= Math.Max(5, o.MinScreenTimeSeconds))
            .OrderByDescending(c => c.ScreenTime(frameSeconds))
            .Take(20)
            .Select((c, i) =>
            {
                var best = c.Tracks.SelectMany(t => t.Observations).MaxBy(ob => ob.Quality)!;
                var (screen, intervals) = TimelineBuilder.Spans(c.Tracks, frameSeconds, o.SceneGapSeconds);
                var guess = cast.Where(p => p.References.Count > 0)
                    .Select(p => (p.PersonId, S: CastMatcher.Score(c.Centroid, p.References)))
                    .OrderByDescending(x => x.S)
                    .FirstOrDefault();
                return new UnknownFace
                {
                    Id = i,
                    ScreenTimeSeconds = screen,
                    Intervals = intervals,
                    ThumbTime = best.Time,
                    ThumbBox = [best.Box.X1 / frameW, best.Box.Y1 / frameH, best.Box.X2 / frameW, best.Box.Y2 / frameH],
                    Embedding = EmbeddingCodec.Encode(c.Centroid),
                    SuggestedPersonId = guess.S >= 0.15f ? guess.PersonId : null,
                };
            })
            .ToList();
        var diagnostics = clusters
            .OrderByDescending(c => c.ScreenTime(frameSeconds))
            .Select(c =>
            {
                var ranked = cast.Where(p => p.References.Count > 0)
                    .Select(p => (p.Name, S: CastMatcher.Score(c.Centroid, p.References)))
                    .OrderByDescending(x => x.S).Take(2).ToList();
                var named = matches.FirstOrDefault(m => m.Cluster == c)?.Person.Name ?? "-";
                return $"cluster tracks={c.Tracks.Count} screen={c.ScreenTime(frameSeconds):F0}s -> {named} | "
                    + string.Join(", ", ranked.Select(r => $"{r.Name} {r.S:F3}"))
                    + " @ " + string.Join(" ", c.Tracks.OrderBy(t => t.Start).Take(4).Select(t => $"{t.Start:F0}-{t.End:F0}"));
            })
            .ToList();

        // Only very clear, well-covered matches teach the gallery: a wrong one would spread.
        var learned = matches
            .Where(m => m.Score >= o.IdentifyThreshold + 0.1f && m.Margin >= o.IdentifyMargin * 2 && m.Cluster.ScreenTime(frameSeconds) >= 10)
            .Select(m => (m.Person.PersonId, m.Cluster.Centroid, m.Score))
            .ToList();
        diagnostics.Insert(0, $"time total={sw.Elapsed.TotalSeconds:F1}s detect={tDetect.Elapsed.TotalSeconds:F1}s align={tAlign.Elapsed.TotalSeconds:F1}s embed={tEmbed.Elapsed.TotalSeconds:F1}s");

        return new FaceAnalysisResult
        {
            People = people,
            Unknown = unknown,
            FrameColors = colors,
            Learned = learned,
            Diagnostics = diagnostics,
            Stats = new AnalysisStats
            {
                Frames = frames,
                Faces = faces,
                Tracks = finished.Count,
                Clusters = clusters.Count,
                Identified = people.Count,
                Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
            },
        };
    }

    private static FaceObservation Observe(RgbImage frame, int index, double time, FaceBox box)
    {
        // Every face is aligned and may be embedded: ArcFace still recognises three-quarter views
        // well, and dropping them lost actors in wide shots. Off-angle and blurry crops only rank
        // lower, so a track's best (embedded) crops are its most frontal, sharpest ones.
        var crop = FaceAligner.Align(frame, box.Landmarks);
        float sharp = Math.Clamp(FaceAligner.Sharpness(crop) / 150f, 0.2f, 1f);
        float pose = 0.25f + (0.75f * box.Frontality());
        float quality = box.Score * Math.Min(1f, box.Height / 112f) * pose * sharp;
        return new FaceObservation { Frame = index, Time = time, Box = box, Quality = quality, Crop = crop };
    }

    private void EmbedTracks(List<FaceTrack> tracks)
    {
        var crops = new List<RgbImage>();
        var owners = new List<(FaceTrack Track, FaceObservation Obs)>();
        foreach (var t in tracks)
        {
            foreach (var o in t.Best)
            {
                if (o.Crop is not null)
                {
                    crops.Add(o.Crop);
                    owners.Add((t, o));
                }
            }
        }

        if (crops.Count == 0)
        {
            return;
        }

        var emb = _engine.Embed(crops);
        foreach (var g in owners.Select((ow, i) => (ow.Track, ow.Obs, E: emb[i])).GroupBy(x => x.Track))
        {
            var list = g.ToList();

            // A track whose crops disagree probably swapped people mid-shot; trust none of it.
            bool consistent = true;
            for (int i = 0; i < list.Count && consistent; i++)
            {
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (Vec.Dot(list[i].E, list[j].E) < 0.2f)
                    {
                        consistent = false;
                        break;
                    }
                }
            }

            if (consistent)
            {
                g.Key.Embedding = Vec.Mean(list.Select(x => (x.E, x.Obs.Quality)), _engine.EmbeddingSize);
            }

            foreach (var x in list)
            {
                x.Obs.Crop = null;
            }
        }
    }
}

/// <summary>Turns cast photos into reference embeddings.</summary>
public static class ReferenceFaces
{
    /// <summary>
    /// Embeds the single clear face in a portrait (original and mirrored, averaged). Returns
    /// null for photos with no face, or with two faces of similar size (ambiguous).
    /// </summary>
    public static float[]? Embed(IFaceEngine engine, RgbImage photo)
    {
        var boxes = engine.Detect(photo, 0.5f).Where(b => b.Score >= 0.6f).OrderByDescending(b => b.Area).ToList();
        if (boxes.Count == 0 || (boxes.Count > 1 && boxes[1].Area > boxes[0].Area * 0.6f))
        {
            return null;
        }

        var crop = FaceAligner.Align(photo, boxes[0].Landmarks);
        var e = engine.Embed([crop, crop.FlipHorizontal()]);
        return Vec.Mean([(e[0], 1f), (e[1], 1f)], engine.EmbeddingSize);
    }
}
