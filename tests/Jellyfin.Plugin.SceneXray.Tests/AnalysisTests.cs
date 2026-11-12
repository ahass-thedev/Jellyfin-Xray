using Jellyfin.Plugin.SceneXray.Analysis;
using Jellyfin.Plugin.SceneXray.Music;
using Jellyfin.Plugin.SceneXray.Sharing;
using Jellyfin.Plugin.SceneXray.Vision;
using Xunit;

namespace Jellyfin.Plugin.SceneXray.Tests;

public class AnalysisTests
{
    private static readonly Random Rng = new(7);

    /// <summary>A random unit vector: a stand-in identity.</summary>
    private static float[] Identity(int dim = 64)
    {
        var v = new float[dim];
        for (int i = 0; i < dim; i++)
        {
            v[i] = (float)((Rng.NextDouble() * 2) - 1);
        }

        Vec.Normalize(v);
        return v;
    }

    /// <summary>The same identity seen through noise (a different frame / angle).</summary>
    private static float[] Noisy(float[] id, float noise)
    {
        var v = id.Select(x => x + (float)(((Rng.NextDouble() * 2) - 1) * noise)).ToArray();
        Vec.Normalize(v);
        return v;
    }

    private static FaceObservation Obs(int frame, float x, float q = 1) =>
        new() { Frame = frame, Time = frame, Box = new FaceBox(x, 10, x + 50, 70, 0.9f, new float[10]), Quality = q };

    private static FaceTrack Track(int[] frames, float[] embedding, float x = 0)
    {
        var tracker = new FaceTracker();
        foreach (int f in frames)
        {
            tracker.Push(f, false, [Obs(f, x)]);
        }

        tracker.CloseAll();
        var t = tracker.DrainClosed().Single();
        t.Embedding = embedding;
        return t;
    }

    [Fact]
    public void Tracker_LinksOverlappingBoxes_AndSplitsOnShotCut()
    {
        var tracker = new FaceTracker();
        tracker.Push(0, false, [Obs(0, 0), Obs(0, 300)]);
        tracker.Push(1, false, [Obs(1, 5), Obs(1, 305)]);
        tracker.Push(2, true, [Obs(2, 5)]); // cut: same place, new shot
        tracker.CloseAll();
        var tracks = tracker.DrainClosed();
        Assert.Equal(3, tracks.Count);
        Assert.Equal(2, tracks.Count(t => t.Observations.Count == 2));
    }

    [Fact]
    public void Tracker_KeepsOnlyBestCrops()
    {
        var tracker = new FaceTracker(keepBest: 2);
        for (int f = 0; f < 5; f++)
        {
            var o = Obs(f, 0, q: f);
            o.Crop = new RgbImage(2, 2);
            tracker.Push(f, false, [o]);
        }

        tracker.CloseAll();
        var t = tracker.DrainClosed().Single();
        Assert.Equal([4f, 3f], t.Best.Select(b => b.Quality));
        Assert.Equal(2, t.Observations.Count(o => o.Crop is not null));
    }

    [Fact]
    public void Clusterer_GroupsSamePerson_ButNeverTwoFacesFromOneFrame()
    {
        var a = Identity();
        var b = Identity();
        var tracks = new List<FaceTrack>
        {
            Track([0, 1, 2], Noisy(a, 0.05f)),
            Track([10, 11], Noisy(a, 0.05f)),
            Track([0, 1], Noisy(b, 0.05f), 300),
            Track([20], Noisy(b, 0.05f)),
        };

        var clusters = TrackClusterer.Cluster(tracks, 0.45f);
        Assert.Equal(2, clusters.Count);
        Assert.All(clusters, c => Assert.Equal(2, c.Tracks.Count));

        // Twins: identical embeddings, but on screen together, so they must stay apart.
        var twin1 = Track([30, 31, 32], a);
        var twin2 = Track([30, 31, 32], a, 300);
        Assert.Equal(2, TrackClusterer.Cluster([twin1, twin2], 0.45f).Count);
    }

    [Fact]
    public void Matcher_NamesClearMatches_AndLeavesStrangersUnnamed()
    {
        var actor = Identity();
        var other = Identity();
        var stranger = Identity();
        var cast = new List<CastCandidate>
        {
            new() { PersonId = Guid.NewGuid(), Name = "Actor" },
            new() { PersonId = Guid.NewGuid(), Name = "Other" },
        };
        cast[0].References.Add(Noisy(actor, 0.15f));
        cast[1].References.Add(Noisy(other, 0.15f));

        var clusters = TrackClusterer.Cluster(
            [Track([0, 1, 2, 3], Noisy(actor, 0.05f)), Track([10, 11, 12, 13], Noisy(stranger, 0.05f), 300)], 0.45f);
        var named = CastMatcher.Match(clusters, cast, 1, 0.32f, 0.06f);

        Assert.Single(named);
        Assert.Equal("Actor", named[0].Person.Name);
    }

    [Fact]
    public void Matcher_WeakTier_NeedsAWideLead()
    {
        var cast = new List<CastCandidate>
        {
            new() { PersonId = Guid.NewGuid(), Name = "A" },
            new() { PersonId = Guid.NewGuid(), Name = "B" },
        };

        // Build a centroid with similarity ~0.25 to A's photo and ~0.0 to B's.
        var photoA = Identity(512);
        var photoB = Identity(512);
        var noise = Identity(512);
        float along = Vec.Dot(noise, photoA), alongB = Vec.Dot(noise, photoB);
        noise = noise.Select((x, i) => x - (along * photoA[i]) - (alongB * photoB[i])).ToArray(); // orthogonal to both photos
        Vec.Normalize(noise);
        var centroid = photoA.Zip(noise, (p, n) => (0.25f * p) + (0.9682f * n)).ToArray(); // cos = 0.25 exactly
        cast[0].References.Add(photoA);
        cast[1].References.Add(photoB);

        var cluster = TrackClusterer.Cluster([Track([0, 1, 2, 3, 4], centroid)], 0.45f);
        Assert.Single(CastMatcher.Match(cluster, cast, 1, 0.32f, 0.06f, weakThreshold: 0.2f));
        Assert.Empty(CastMatcher.Match(cluster, cast, 1, 0.32f, 0.06f, weakThreshold: 0f));
    }

    [Fact]
    public void Spans_MergeSceneGaps_AndCountOverlapOnce()
    {
        var a = Track([0, 1, 2], Identity());
        var b = Track([2, 3], Identity());
        var c = Track([20, 21], Identity());
        var (screen, intervals) = TimelineBuilder.Spans([a, b, c], 1, 6);
        Assert.Equal(5.5, screen); // 0..3.5 (-0.5 clamped to 0) + 19.5..21.5
        Assert.Equal(2, intervals.Count);
        Assert.Equal([0, 3.5], intervals[0]);
    }

    [Fact]
    public void Similarity_RecoversRotationScaleTranslation()
    {
        float[] src = [10, 10, 30, 10, 20, 20, 12, 30, 28, 30];
        double angle = 0.3, scale = 1.7;
        var dst = new float[10];
        for (int i = 0; i < 5; i++)
        {
            double x = src[2 * i], y = src[(2 * i) + 1];
            dst[2 * i] = (float)((scale * ((Math.Cos(angle) * x) - (Math.Sin(angle) * y))) + 5);
            dst[(2 * i) + 1] = (float)((scale * ((Math.Sin(angle) * x) + (Math.Cos(angle) * y))) - 3);
        }

        var (a, b, tx, ty) = FaceAligner.EstimateSimilarity(src, dst);
        Assert.Equal(scale * Math.Cos(angle), a, 3);
        Assert.Equal(scale * Math.Sin(angle), b, 3);
        Assert.Equal(5, tx, 2);
        Assert.Equal(-3, ty, 2);
    }

    [Fact]
    public void Fingerprints_FindASongUnderDialogueNoise()
    {
        var songs = Enumerable.Range(0, 20).Select(_ => Enumerable.Range(0, 1500).Select(_ => (uint)Rng.Next()).ToArray()).ToList();
        var matcher = new FingerprintMatcher(songs);

        // 10 minutes of unrelated audio with song 7 (from its 200th point) mixed in at point 2000
        // for ~60 s, with 15% of bits flipped and every third point garbled by dialogue.
        var query = Enumerable.Range(0, 5000).Select(_ => (uint)Rng.Next()).ToArray();
        for (int i = 0; i < 480; i++)
        {
            uint v = songs[7][200 + i];
            for (int bit = 0; bit < 32; bit++)
            {
                if (Rng.NextDouble() < 0.15)
                {
                    v ^= 1u << bit;
                }
            }

            query[2000 + i] = i % 3 == 0 ? (uint)Rng.Next() : v;
        }

        var found = matcher.Match(query, 8);
        var m = Assert.Single(found);
        Assert.Equal(7, m.Track);
        Assert.InRange(m.Start, (2000 * FingerprintMatcher.ItemSeconds) - 3, (2000 * FingerprintMatcher.ItemSeconds) + 3);
        Assert.InRange(m.End - m.Start, 50, 62);
    }

    [Fact]
    public void Subtitles_MusicMarkersBecomeRegions()
    {
        const string Srt = "1\r\n00:01:00,000 --> 00:01:03,000\r\nHello.\r\n\r\n2\r\n00:02:00,000 --> 00:02:04,000\r\n♪ Earth angel ♪\r\n\r\n3\r\n00:02:06,000 --> 00:02:09,500\r\n♪ My darling dear ♪\r\n\r\n4\r\n00:05:00,000 --> 00:05:02,000\r\n[ROCK MUSIC PLAYING]\r\n";
        var regions = MusicService.ParseMusicCues(Srt);
        Assert.Equal(2, regions.Count);
        Assert.Equal((120.0, 129.5), regions[0]);
        Assert.Equal((300.0, 302.0), regions[1]);
    }
}
