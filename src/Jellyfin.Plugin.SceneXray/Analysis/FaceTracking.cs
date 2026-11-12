using Jellyfin.Plugin.SceneXray.Vision;

namespace Jellyfin.Plugin.SceneXray.Analysis;

/// <summary>A face found in one sampled frame, with its aligned crop and a quality score.</summary>
public sealed class FaceObservation
{
    public required int Frame { get; init; }

    public required double Time { get; init; }

    public required FaceBox Box { get; init; }

    public required float Quality { get; init; }

    /// <summary>Gets or sets the aligned crop; released once the track is embedded.</summary>
    public RgbImage? Crop { get; set; }
}

/// <summary>One person followed across consecutive sampled frames of a single shot.</summary>
public sealed class FaceTrack
{
    internal FaceTrack(int id) => Id = id;

    public int Id { get; }

    public List<FaceObservation> Observations { get; } = [];

    /// <summary>Gets the best observations kept for embedding (highest quality first).</summary>
    public List<FaceObservation> Best { get; } = [];

    /// <summary>Gets or sets the quality-weighted mean embedding.</summary>
    public float[]? Embedding { get; set; }

    public double Start => Observations[0].Time;

    public double End => Observations[^1].Time;

    public float TotalQuality => Observations.Sum(o => o.Quality);

    public FaceBox LastBox => Observations[^1].Box;

    internal int LastFrame => Observations[^1].Frame;
}

/// <summary>
/// Links detections frame to frame by box overlap within a shot. Linking is purely geometric
/// so that only a handful of crops per track need embedding; identity is resolved later by
/// clustering whole tracks, not single frames.
/// </summary>
public sealed class FaceTracker
{
    private readonly int _keepBest;
    private readonly int _maxGap;
    private readonly float _minIoU;
    private readonly List<FaceTrack> _active = [];
    private readonly List<FaceTrack> _closed = [];
    private int _nextId;

    public FaceTracker(int keepBest = 3, int maxGapFrames = 1, float minIoU = 0.25f)
    {
        _keepBest = keepBest;
        _maxGap = maxGapFrames;
        _minIoU = minIoU;
    }

    /// <summary>Adds one sampled frame's faces. <paramref name="shotCut"/> ends every open track.</summary>
    public void Push(int frame, bool shotCut, IReadOnlyList<FaceObservation> faces)
    {
        if (shotCut)
        {
            CloseAll();
        }

        // Expire tracks that have not been seen for too long.
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (frame - _active[i].LastFrame > _maxGap + 1)
            {
                Close(i);
            }
        }

        // Greedy assignment on IoU, best pairs first.
        var pairs = new List<(float IoU, int T, int F)>();
        for (int t = 0; t < _active.Count; t++)
        {
            for (int f = 0; f < faces.Count; f++)
            {
                float iou = _active[t].LastBox.IoU(faces[f].Box);
                if (iou >= _minIoU)
                {
                    pairs.Add((iou, t, f));
                }
            }
        }

        var usedT = new bool[_active.Count];
        var usedF = new bool[faces.Count];
        foreach (var (_, t, f) in pairs.OrderByDescending(p => p.IoU))
        {
            if (usedT[t] || usedF[f])
            {
                continue;
            }

            usedT[t] = usedF[f] = true;
            Append(_active[t], faces[f]);
        }

        for (int f = 0; f < faces.Count; f++)
        {
            if (!usedF[f])
            {
                var track = new FaceTrack(_nextId++);
                Append(track, faces[f]);
                _active.Add(track);
            }
        }
    }

    /// <summary>Returns tracks closed since the last call.</summary>
    public List<FaceTrack> DrainClosed()
    {
        var c = _closed.ToList();
        _closed.Clear();
        return c;
    }

    public void CloseAll()
    {
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            Close(i);
        }
    }

    private void Close(int i)
    {
        _closed.Add(_active[i]);
        _active.RemoveAt(i);
    }

    private void Append(FaceTrack track, FaceObservation o)
    {
        track.Observations.Add(o);
        track.Best.Add(o);
        track.Best.Sort((a, b) => b.Quality.CompareTo(a.Quality));
        if (track.Best.Count > _keepBest)
        {
            track.Best[^1].Crop = null; // free pixels we will never embed
            track.Best.RemoveAt(track.Best.Count - 1);
        }

        // Crops are only needed for the kept best observations.
        if (!track.Best.Contains(o))
        {
            o.Crop = null;
        }
    }
}

/// <summary>A group of tracks believed to be one person.</summary>
public sealed class PersonCluster
{
    public List<FaceTrack> Tracks { get; } = [];

    public HashSet<int> Frames { get; } = [];

    public float[] Centroid { get; set; } = [];

    public double ScreenTime(double frameSeconds) => Tracks.Sum(t => (t.End - t.Start) + frameSeconds);

    internal float[] Sum { get; set; } = [];

    internal float Weight { get; set; }

    internal void Add(FaceTrack t, float w)
    {
        if (Sum.Length == 0)
        {
            Sum = new float[t.Embedding!.Length];
        }

        Tracks.Add(t);
        foreach (var o in t.Observations)
        {
            Frames.Add(o.Frame);
        }

        for (int i = 0; i < Sum.Length; i++)
        {
            Sum[i] += t.Embedding![i] * w;
        }

        Weight += w;
        Centroid = (float[])Sum.Clone();
        Vec.Normalize(Centroid);
    }

    internal void Absorb(PersonCluster other)
    {
        Tracks.AddRange(other.Tracks);
        Frames.UnionWith(other.Frames);
        for (int i = 0; i < Sum.Length; i++)
        {
            Sum[i] += other.Sum[i];
        }

        Weight += other.Weight;
        Centroid = (float[])Sum.Clone();
        Vec.Normalize(Centroid);
    }
}

/// <summary>
/// Groups tracks into people. Two rules carry the accuracy: a track's identity comes from
/// several good crops rather than one frame, and two tracks visible in the same frame can
/// never be the same person (cannot-link), which stops look-alikes from collapsing together.
/// </summary>
public static class TrackClusterer
{
    public static List<PersonCluster> Cluster(IEnumerable<FaceTrack> tracks, float threshold)
    {
        var clusters = new List<PersonCluster>();

        // Leader pass: strongest, longest tracks seed clusters first.
        foreach (var t in tracks.Where(t => t.Embedding is not null).OrderByDescending(t => t.TotalQuality))
        {
            PersonCluster? best = null;
            float bestSim = threshold;
            var frames = t.Observations.Select(o => o.Frame).ToList();
            foreach (var c in clusters)
            {
                float s = Vec.Dot(c.Centroid, t.Embedding);
                if (s >= bestSim && !FrameSets.Conflict(c.Frames, frames))
                {
                    best = c;
                    bestSim = s;
                }
            }

            if (best is null)
            {
                best = new PersonCluster();
                clusters.Add(best);
            }

            best.Add(t, Math.Max(0.05f, t.TotalQuality));
        }

        // Merge pass: join clusters whose centroids agree and which never share a frame.
        // Each round merges every disjoint best pair at once (O(n^2) per round, few rounds).
        while (true)
        {
            var pairs = new List<(float S, int I, int J)>();
            for (int i = 0; i < clusters.Count; i++)
            {
                for (int j = i + 1; j < clusters.Count; j++)
                {
                    float s = Vec.Dot(clusters[i].Centroid, clusters[j].Centroid);
                    if (s >= threshold)
                    {
                        pairs.Add((s, i, j));
                    }
                }
            }

            var touched = new HashSet<int>();
            var gone = new HashSet<int>();
            foreach (var (_, i, j) in pairs.OrderByDescending(p => p.S))
            {
                if (touched.Contains(i) || touched.Contains(j) || FrameSets.Conflict(clusters[i].Frames, clusters[j].Frames))
                {
                    continue;
                }

                clusters[i].Absorb(clusters[j]);
                touched.Add(i);
                touched.Add(j);
                gone.Add(j);
            }

            if (gone.Count == 0)
            {
                return clusters;
            }

            clusters = clusters.Where((_, idx) => !gone.Contains(idx)).ToList();
        }
    }
}

/// <summary>The "never in the same frame twice" rule, tolerant of mirrors and photos-in-shot.</summary>
public static class FrameSets
{
    public static bool Conflict(HashSet<int> a, IReadOnlyCollection<int> b)
    {
        int shared = b.Count(a.Contains);
        int allowed = Math.Max(1, (int)(0.05 * Math.Min(a.Count, b.Count)));
        return shared > allowed;
    }
}
