using System.Text.Json;
using Jellyfin.Plugin.SceneXray.Sharing;
using Xunit;

namespace Jellyfin.Plugin.SceneXray.Tests;

/// <summary>Alignment of shared timelines (design §3) on synthetic fingerprints.</summary>
public class AlignmentTests
{
    private const double Item = FingerprintMatcher.ItemSeconds;

    private static int Items(double seconds) => (int)Math.Round(seconds / Item);

    private static uint[] Random(int items, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, items).Select(_ => (uint)rng.NextInt64(0, 1L << 32)).ToArray();
    }

    // Each bit flipped with probability p: a re-encode or a different mix of the same audio.
    private static uint[] Noisy(uint[] fp, double p, int seed)
    {
        var rng = new Random(seed);
        return fp.Select(v =>
        {
            for (int b = 0; b < 32; b++)
            {
                if (rng.NextDouble() < p)
                {
                    v ^= 1u << b;
                }
            }

            return v;
        }).ToArray();
    }

    private static uint[] Cut(uint[] fp, double at, double seconds) =>
        fp.Take(Items(at)).Concat(fp.Skip(Items(at + seconds))).ToArray();

    private static uint[] Insert(uint[] fp, double at, double seconds, int seed) =>
        fp.Take(Items(at)).Concat(Random(Items(seconds), seed)).Concat(fp.Skip(Items(at))).ToArray();

    // The local file plays `speed` times as fast (PAL: 25/23.976), with pitch kept so items survive.
    private static uint[] Speed(uint[] fp, double speed) =>
        Enumerable.Range(0, (int)(fp.Length / speed)).Select(i => fp[Math.Min(fp.Length - 1, (int)Math.Round(i * speed))]).ToArray();

    private static AlignmentResult Align(uint[] canonical, uint[] local)
    {
        double runtime = canonical.Length * Item;
        var all = AnchorSet.Extract(canonical, AnchorSet.EverySecondsFor(runtime));
        return Aligner.Align(all.Reference(AnchorSet.ReferenceStride(runtime)), local, runtime, local.Length * Item);
    }

    private static readonly uint[] Film = Random(Items(60 * 60), 1);

    [Fact]
    public void AlignsAnotherReleaseWithADifferentIntroAndCredits()
    {
        // WEB release: 30 s of extra logos in front, 2 minutes of credits trimmed, re-encoded.
        var local = Noisy(Random(Items(30), 2).Concat(Film.Take(Film.Length - Items(120))).ToArray(), 0.08, 3);
        var r = Align(Film, local);

        Assert.True(r.Accepted, r.Verdict);
        Assert.Equal("plain", r.Kind);
        Assert.InRange(r.Slope, 0.9995, 1.0005);
        Assert.InRange(r.Offset, 29.8, 30.2);
        Assert.True(r.RmsSeconds < 0.1);
    }

    [Fact]
    public void AlignsWhenTheLocalFileStartsLater()
    {
        var local = Noisy(Film.Skip(Items(45)).ToArray(), 0.05, 4);
        var r = Align(Film, local);

        Assert.True(r.Accepted, r.Verdict);
        Assert.InRange(r.Offset, -45.2, -44.8);
        Assert.InRange(r.ToLocal(600), 554.8, 555.2);
    }

    [Fact]
    public void AlignsAPalSpeedUpWhosePitchSurvives()
    {
        var r = Align(Film, Noisy(Speed(Film, Chromaprint.PalRatio), 0.05, 5));

        Assert.True(r.Accepted, r.Verdict);
        Assert.InRange(r.Slope * Chromaprint.PalRatio, 0.9995, 1.0005);
    }

    [Theory]
    [InlineData(0.2, 10)]
    [InlineData(0.2, 300)]
    [InlineData(0.35, 30)]
    [InlineData(0.5, 10)]
    [InlineData(0.5, 120)]
    [InlineData(0.65, 30)]
    [InlineData(0.8, 10)]
    [InlineData(0.8, 300)]
    [InlineData(0.9, 60)]
    public void RejectsADifferentCut(double position, double seconds)
    {
        double at = position * Film.Length * Item;
        var shorter = Noisy(Cut(Film, at, seconds), 0.05, 6);
        var longer = Noisy(Insert(Film, at, seconds, 7), 0.05, 8);

        Assert.False(Align(Film, shorter).Accepted, $"scene of {seconds} s removed at {position:P0} was accepted");
        Assert.False(Align(Film, longer).Accepted, $"scene of {seconds} s added at {position:P0} was accepted");
        Assert.False(Align(shorter, Film).Accepted, $"local extended cut ({seconds} s at {position:P0}) was accepted");
    }

    // A dub: dialogue differs, music and effects stay. Every other 40 s block is replaced.
    private static uint[] Dubbed(uint[] fp, int seed)
    {
        var rng = new Random(seed);
        int block = Items(40);
        return fp.Select((v, i) => (i / block) % 2 == 1 ? (uint)rng.NextInt64(0, 1L << 32) : v).ToArray();
    }

    // Net-zero edit: 60 s removed at 40 %, 60 s of new audio inserted at 50 %. Same runtime.
    private static uint[] Shifted(uint[] fp, int seed)
    {
        double t = fp.Length * Item;
        var cut = Cut(fp, 0.4 * t, 60);
        return Insert(cut, (0.5 * t) - 60, 60, seed);
    }

    [Fact]
    public void RejectsADubTooSparseToCheckForCuts()
    {
        // The fit itself is right, but with half the anchors silent a short shifted scene could hide.
        var r = Align(Film, Noisy(Dubbed(Film, 20), 0.05, 21));

        Assert.False(r.Accepted);
        Assert.InRange(r.Offset, -0.2, 0.2);
        Assert.Contains(r.Reasons, s => s.StartsWith("inliers", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsANetZeroEditEvenInADub()
    {
        var plain = Align(Film, Noisy(Shifted(Film, 22), 0.05, 23));
        var dub = Align(Film, Noisy(Dubbed(Shifted(Film, 24), 25), 0.05, 26));

        Assert.False(plain.Accepted, plain.Verdict);
        Assert.False(dub.Accepted, dub.Verdict);
        Assert.Contains(dub.Reasons, s => s.Contains("second offset", StringComparison.Ordinal));
    }

    [Fact]
    public void RepeatedMusicIsNoSecondOffset()
    {
        // The same 30 s passage occurs twice: its anchors also match at the other occurrence,
        // but they match on the line too, so that is no evidence of a cut.
        var film = (uint[])Film.Clone();
        Array.Copy(film, Items(600), film, Items(1200), Items(30));
        var r = Align(film, Noisy(film, 0.05, 27));

        Assert.True(r.Accepted, r.Verdict);
    }

    [Fact]
    public void ACutInsideTheEndSlackOnlyLosesData()
    {
        // 40 s removed 2 minutes before the end: indistinguishable from trimmed credits, so the fit
        // may stand, but the valid range must stop before the cut so nothing after it is misplaced.
        double at = (Film.Length * Item) - 120;
        var r = Align(Film, Cut(Film, at, 40));

        Assert.True(!r.Accepted || r.ValidTo <= at + 1, r.Verdict + $" valid to {r.ValidTo}");
    }

    [Fact]
    public void RejectsUnrelatedAudio()
    {
        var r = Align(Film, Random(Film.Length, 9));

        Assert.False(r.Accepted);
    }

    [Fact]
    public void PalRetryReFingerprintsWithPitchUndone()
    {
        // A PAL file with its pitch shifted: its own fingerprint matches nothing. Re-fingerprinting
        // it with tempo and pitch undone gives the canonical audio stretched back to film speed.
        var pal = Speed(Film, Chromaprint.PalRatio);
        var plainFp = Random(pal.Length, 10);
        string? filter = null;
        var all = AnchorSet.Extract(Film);
        var r = Aligner.AlignAsync(all.Reference(3), plainFp, Film.Length * Item, pal.Length * Item, (f, _) =>
        {
            filter = f;
            return Task.FromResult(Noisy(Speed(pal, 1 / Chromaprint.PalRatio), 0.05, 11));
        }).GetAwaiter().GetResult();

        Assert.Contains("asetrate=", filter);
        Assert.True(r.Accepted, r.Verdict);
        Assert.Equal("pal", r.Kind);
        Assert.False(r.OtherAttempt!.Accepted);
        Assert.InRange(r.Slope * Chromaprint.PalRatio, 0.999, 1.001);
    }

    [Fact]
    public void PalSpeedFollowsTheRuntimes()
    {
        double film = 7200, pal = film / Chromaprint.PalRatio;
        Assert.Equal(1 / Chromaprint.PalRatio, Aligner.PalSpeed(film, pal)!.Value, 6);
        Assert.Equal(Chromaprint.PalRatio, Aligner.PalSpeed(pal, film)!.Value, 6);
        Assert.NotNull(Aligner.PalSpeed(film, pal + 20 - 90)); // other logo, trimmed credits
        Assert.NotNull(Aligner.PalSpeed(1320, (1320 / Chromaprint.PalRatio) - 70)); // the same on an episode
        Assert.Null(Aligner.PalSpeed(film, film));
        Assert.Null(Aligner.PalSpeed(film, film - 30)); // closer to film speed than to PAL
        Assert.Null(Aligner.PalSpeed(film, pal * 0.9));
    }

    private static List<AnchorPair> Line(double a, double b, int n, double every = 60, Func<int, double>? noise = null) =>
        Enumerable.Range(0, n).Select(i => new AnchorPair(i, i * every, (a * i * every) + b + (noise?.Invoke(i) ?? 0), 0.1)).ToList();

    // Canonical and local both run n anchors long (offsets are small against the 3-minute end slack).
    private static AlignmentResult Fit(List<AnchorPair> pairs, int n, AlignmentOptions? o = null) =>
        Aligner.Fit(pairs, Enumerable.Range(0, n).Select(i => i * 60.0).ToList(), 3, n * 60, n * 60, o);

    [Fact]
    public void FitRecoversTheLineDespiteOutliers()
    {
        var pairs = Line(1, 12.5, 100);
        var rng = new Random(12);
        for (int i = 0; i < 100; i += 4)
        {
            pairs.Add(new AnchorPair(i, i * 60, rng.NextDouble() * 6000, 0.3)); // a second, wrong location
        }

        var r = Fit(pairs, 100);

        Assert.True(r.Accepted, r.Verdict);
        Assert.Equal(100, r.Inliers);
        Assert.InRange(r.Offset, 12.49, 12.51);
    }

    [Fact]
    public void FitRejectsAnImplausibleSlope()
    {
        var r = Fit(Line(1.01, 0, 100), 100);

        Assert.False(r.Accepted);
    }

    [Fact]
    public void FitRejectsLargeResiduals()
    {
        var rng = new Random(13);
        var o = new AlignmentOptions { InlierToleranceSeconds = 2 };
        var r = Fit(Line(1, 0, 100, noise: _ => (rng.NextDouble() - 0.5) * 1.8), 100, o);

        Assert.False(r.Accepted);
        Assert.Contains(r.Reasons, s => s.StartsWith("RMS", StringComparison.Ordinal));
    }

    [Fact]
    public void FitRejectsAShiftedStretchInTheMiddle()
    {
        // Same line on both sides, 5 minutes in between found 400 s later: a moved scene.
        var pairs = Line(1, 0, 100).Where(p => p.Anchor is < 40 or > 44).ToList();
        pairs.AddRange(Enumerable.Range(40, 5).Select(i => new AnchorPair(i, i * 60, (i * 60) + 400, 0.2)));
        var r = Fit(pairs, 100);

        Assert.False(r.Accepted);
        Assert.Equal(5, r.SecondLineAnchors);
        Assert.InRange(r.SecondLineShift, 399.5, 400.5);
    }

    [Fact]
    public void FitRejectsAHoleLongerThanTheLimit()
    {
        // 40 minutes of informative anchors that match nowhere, between two agreeing stretches.
        var pairs = Line(1, 0, 100).Where(p => p.Anchor is < 30 or > 69).ToList();
        var r = Fit(pairs, 100, new AlignmentOptions { MinInlierFraction = 0.5 });

        Assert.False(r.Accepted);
        Assert.Contains(r.Reasons, s => s.StartsWith("hole", StringComparison.Ordinal));
    }

    [Fact]
    public void SilenceBetweenInliersIsNoHole()
    {
        // Anchors 40-44 are silence: not informative, so never considered, and their gap is fine.
        var pairs = Line(1, 0, 100).Where(p => p.Anchor is < 40 or > 44).ToList();
        var times = Enumerable.Range(0, 100).Where(i => i is < 40 or > 44).Select(i => i * 60.0).ToList();
        var r = Aligner.Fit(pairs, times, 3, 6000, 6000);

        Assert.True(r.Accepted, r.Verdict);
    }
}

public class AnchorTests
{
    [Fact]
    public void ExtractsOneWindowEveryTwentySeconds()
    {
        var fp = Enumerable.Range(0, 1000).Select(i => (uint)i).ToArray(); // 123.8 s
        var set = AnchorSet.Extract(fp);

        Assert.Equal(7, set.Count); // 0, 20, ... 120 s; the window at 140 s would run past the end
        Assert.Equal(24, set.Windows[0].Length);
        Assert.Equal(162, set.StartItem(1)); // round(20 / 0.1238)
        Assert.Equal(162u, set.Windows[1][0]);
        Assert.InRange(set.Time(1), 20 - 0.07, 20 + 0.07); // within half an item of the grid
    }

    [Fact]
    public void ReferenceKeepsEveryNthAnchorAtItsCanonicalTime()
    {
        var set = AnchorSet.Extract(Enumerable.Range(0, 20000).Select(i => (uint)i).ToArray());
        var reference = set.Reference(3);

        Assert.Equal((set.Count + 2) / 3, reference.Count);
        Assert.Equal(set.Time(3), reference.Time(1), 6);
        Assert.Equal(set.Windows[6], reference.Windows[2]);
        Assert.Equal(3, AnchorSet.ReferenceStride(7200));
        Assert.Equal(2, AnchorSet.ReferenceStride(1320));
        Assert.Equal(10, AnchorSet.EverySecondsFor(1320));
    }

    [Fact]
    public void EncodesAndDecodes()
    {
        var set = AnchorSet.Extract(Enumerable.Range(0, 5000).Select(i => (uint)(i * 2654435761u)).ToArray());
        var dto = SharedAnchors.From(set);
        var back = dto.ToSet();

        Assert.Equal(set.Count, back.Count);
        Assert.Equal(set.Windows[^1], back.Windows[^1]);
        Assert.Equal(set.Count * 96, Convert.FromBase64String(dto.Items).Length);
    }

    [Fact]
    public void SilenceIsNotInformative()
    {
        Assert.False(AnchorSet.IsInformative(new uint[24]));
        Assert.False(AnchorSet.IsInformative(Enumerable.Repeat(0xDEADBEEFu, 24).ToArray()));
        Assert.True(AnchorSet.IsInformative(Enumerable.Range(0, 24).Select(i => (uint)(i * 2654435761u)).ToArray()));
    }

    [Fact]
    public void LocatesAnAnchorInsideALongFingerprint()
    {
        var rng = new Random(14);
        var dense = Enumerable.Range(0, 20000).Select(_ => (uint)rng.NextInt64(0, 1L << 32)).ToArray();
        var anchor = dense.AsSpan(7777, 24).ToArray();
        var located = new FingerprintMatcher([anchor]).Locate(dense);

        Assert.Equal(7777, Assert.Single(located[0]).QueryItem);
    }
}

public class ContributionTests
{
    [Fact]
    public void TitleKeysAreCanonical()
    {
        Assert.Equal("movie:tmdb:603", TitleKey.Movie(603, "tt0133093"));
        Assert.Equal("movie:imdb:tt0133093", TitleKey.Movie(null, " TT0133093 "));
        Assert.Null(TitleKey.Movie(0, "nonsense"));
        Assert.Equal("episode:tvdb:81189:s01e01", TitleKey.Episode(81189, 1396, 1, 1));
        Assert.Equal("episode:tmdb:1396:s05e14", TitleKey.Episode(null, 1396, 5, 14));
        Assert.Null(TitleKey.Episode(null, null, 1, 1));
        Assert.Equal(4, TitleKey.HashPrefix("movie:tmdb:603").Length);
    }

    [Theory]
    [InlineData(null, "theatrical")]
    [InlineData("Theatrical Cut", "theatrical")]
    [InlineData("Extended Edition", "extended")]
    [InlineData("Director's Cut", "directors")]
    [InlineData("IMAX Enhanced", "imax")]
    [InlineData("Unrated", "unrated")]
    [InlineData("Ultimate Fan Restoration", "other")]
    public void EditionsAreNormalised(string? name, string expected) => Assert.Equal(expected, TitleKey.Edition(name));

    [Fact]
    public void ContributionFieldsArePinned()
    {
        // Adding a field to the wire format must be deliberate: update this list in the same change.
        var c = new SharedContribution(
            new SharedTitle("movie", 603, "tt0133093", null, null, null, null, "theatrical", 8160.4, 23.976),
            new SharedAnalyzer("2", "1.3.1.0", "Balanced", 1.0, "wespeaker-r34"),
            new SharedAnchors(20, 24, ""),
            [[0, 41.2]],
            [new SharedPerson(6384, "face", 0.81, [[41.2, 118.0]])],
            [],
            [new SharedUnknownFace(3, "w600k_r50", "", 212, [[1, 2]])],
            [new SharedMusicCue(300.1, 395, "t", "a", "b", null, "library", 0.9)]);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(c));
        string Names(JsonElement e) => string.Join(",", e.EnumerateObject().Select(p => p.Name));
        var root = doc.RootElement;

        Assert.Equal("title,analyzer,anchors,scenes,people,voicePeople,unknownFaces,music", Names(root));
        Assert.Equal("kind,tmdb,imdb,series,season,episode,episodeTmdb,edition,runtime,frameRate", Names(root.GetProperty("title")));
        Assert.Equal("version,plugin,modelPack,sampleFps,voiceModel", Names(root.GetProperty("analyzer")));
        Assert.Equal("every,length,items", Names(root.GetProperty("anchors")));
        Assert.Equal("tmdbPerson,source,confidence,intervals", Names(root.GetProperty("people")[0]));
        Assert.Equal("id,recognizer,embedding,screenTime,intervals", Names(root.GetProperty("unknownFaces")[0]));
        Assert.Equal("start,end,title,artist,album,mbid,source,confidence", Names(root.GetProperty("music")[0]));
    }
}
