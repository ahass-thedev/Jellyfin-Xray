using System.Text.Json;
using Jellyfin.Plugin.SceneXray.Share;
using Jellyfin.Plugin.SceneXray.Sharing;
using Jellyfin.Plugin.SceneXray.Storage;
using Xunit;

namespace Jellyfin.Plugin.SceneXray.Tests;

/// <summary>The plugin side of sharing: what leaves the server, and what a received timeline becomes.</summary>
public class ShareMappingTests
{
    private static readonly Guid Neo = Guid.NewGuid(), Trinity = Guid.NewGuid(), Extra = Guid.NewGuid(), Removed = Guid.NewGuid(), Song = Guid.NewGuid();

    private static readonly SharedTitle Matrix = new("movie", 603, "tt0133093", null, null, null, null, "theatrical", 600, 23.976);

    private static XrayTimeline Local() => new()
    {
        ItemId = Guid.NewGuid(),
        DurationSeconds = 600,
        Signature = "123:456:2:Balanced:1",
        Scenes = [[0, 41.2], [41.2, 118], [118, 600]],
        People =
        [
            new TimelinePerson { PersonId = Neo, Name = "Keanu Reeves", Role = "Neo", Confidence = 0.81, Intervals = [[41.2, 118]], Spans = [[50, 60]] },
            new TimelinePerson { PersonId = Extra, Name = "No TMDb id", Confidence = 0.9, Intervals = [[0, 10]] },
            new TimelinePerson { PersonId = Removed, Name = "Wrongly named", Confidence = 0.9, Intervals = [[0, 10]] },
        ],
        VoicePeople = [new TimelinePerson { PersonId = Trinity, Name = "Carrie-Anne Moss", Confidence = 0.7, Intervals = [[200, 260], [250, 300]], Source = "voice:subtitle" }],
        Music = [new MusicCue { Start = 300.14, End = 395, Title = "Clubbed to Death", Artist = "Rob D", ItemId = Song, Source = "library", Confidence = 0.9 }],
        Unknown = [new UnknownFace { Id = 3, ScreenTimeSeconds = 40, Intervals = [[10, 50]], Embedding = "AAAA", ThumbBox = [0.1f, 0.1f, 0.2f, 0.2f] }],
        RejectedPersonIds = [Removed],
    };

    private static int? Tmdb(Guid id) => id == Neo ? 6384 : id == Trinity ? 530 : id == Removed ? 999 : null;

    private static SharedContribution Contribution(XrayTimeline t) => ShareMapping.Contribution(
        t,
        Matrix,
        new SharedAnalyzer("2", "1.3.1.0", "Balanced", 1, null),
        AnchorSet.Extract(new uint[5000]),
        Tmdb,
        id => id == Song ? "6f3c2c7e-0d0d-4a2b-9c55-0f6c1f0b5f1a" : null);

    [Fact]
    public void OnlyTmdbIdsAndTimesLeaveTheServer()
    {
        var t = Local();
        var c = Contribution(t);
        string json = JsonSerializer.Serialize(c);

        var neo = Assert.Single(c.People);
        Assert.Equal(6384, neo.TmdbPerson);
        Assert.Equal("face", neo.Source);
        Assert.Equal([41.2, 118.0], neo.Intervals[0]);

        // Voice intervals arrive merged and sorted.
        var trinity = Assert.Single(c.VoicePeople);
        Assert.Equal("voice", trinity.Source);
        Assert.Equal([200.0, 300.0], Assert.Single(trinity.Intervals));

        Assert.Equal("6f3c2c7e-0d0d-4a2b-9c55-0f6c1f0b5f1a", Assert.Single(c.Music).Mbid);
        Assert.Empty(c.UnknownFaces);

        // No names, no Jellyfin GUIDs, no raw spans, no local signature, no thumbnails.
        foreach (string leak in new[] { "Keanu", "Carrie", "Neo\"", Neo.ToString(), Song.ToString(), t.ItemId.ToString(), "spans", "123:456", "thumb", "Wrongly" })
        {
            Assert.DoesNotContain(leak, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AReceivedTimelineIsMappedThroughTheFitAndTheLocalCast()
    {
        var c = new ConsensusTimeline(
            Guid.NewGuid(),
            2,
            17,
            ConsensusTimeline.Unconfirmed,
            1,
            600,
            [[0, 100], [100, 600]],
            [new ConsensusPerson(6384, 0.5, 1, [[50, 100]]), new ConsensusPerson(424242, 0.5, 1, [[0, 50]]), new ConsensusPerson(999, 0.5, 1, [[0, 50]])],
            [new ConsensusPerson(530, 0.4, 1, [[590, 640]])],
            []);
        var fit = new SharedInfo { VariantId = c.VariantId, Slope = 1, Offset = 30, ValidFrom = 0, ValidTo = 600 };
        var cast = new Dictionary<int, LocalPerson>
        {
            [6384] = new(Neo, "Keanu Reeves", "Neo"),
            [530] = new(Trinity, "Carrie-Anne Moss", "Trinity"),
            [999] = new(Removed, "Wrongly named", null),
        };
        var previous = new XrayTimeline { RejectedPersonIds = [Removed] };

        var t = ShareMapping.Timeline(c, fit, Guid.NewGuid(), 610, ShareMapping.SharedSignature(c.VariantId, 17, 1000, 2000), cast, _ => null, previous);

        var neo = Assert.Single(t.People);
        Assert.Equal(Neo, neo.PersonId);
        Assert.Equal("Keanu Reeves", neo.Name);
        Assert.Equal([80.0, 130.0], neo.Intervals[0]);

        // 590–640 canonical is 620–670 local, past this file's end: clipped, then dropped.
        Assert.Empty(t.VoicePeople);
        Assert.Equal([30.0, 130.0], t.Scenes[0]);
        Assert.Equal("shared:2", t.Analyzer.Version);
        Assert.Same(fit, t.Shared);
        Assert.Contains(Removed, t.RejectedPersonIds);
    }

    [Fact]
    public void ASharedSignatureIsCurrentUntilTheFileChanges()
    {
        string sig = ShareMapping.SharedSignature(Guid.NewGuid(), 4, 1000, 2000);
        Assert.True(ShareMapping.SharedSignatureMatches(sig, 1000, 2000));
        Assert.False(ShareMapping.SharedSignatureMatches(sig, 1001, 2000));
        Assert.False(ShareMapping.SharedSignatureMatches("1000:2000:2:Balanced:1", 1000, 2000));
    }

    [Theory]
    [InlineData("/m/The Matrix (1999) {edition-Director's Cut}.mkv", "directors")]
    [InlineData("/m/Blade Runner (1982) - Final Cut.mkv", "other")]
    [InlineData("/m/Aliens (1986) - Extended.mkv", "extended")]
    [InlineData("/m/Heat (1995).mkv", "theatrical")]
    public void TheEditionIsALabelNeverTheFileName(string path, string edition) =>
        Assert.Equal(edition, ShareMapping.Edition(path));
}

public class UnknownFaceShareTests
{
    private static float[] Face(int seed)
    {
        var rng = new Random(seed);
        var v = Enumerable.Range(0, 512).Select(_ => (float)(rng.NextDouble() - 0.5)).ToArray();
        float n = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / n).ToArray();
    }

    [Fact]
    public void OnlyTheSignatureOfARecurringUnknownFaceIsSent()
    {
        var t = new XrayTimeline
        {
            Unknown =
            [
                new UnknownFace { Id = 3, ScreenTimeSeconds = 40, Intervals = [[10, 30], [25, 50]], Embedding = Analysis.EmbeddingCodec.Encode(Face(1)), ThumbTime = 12, ThumbBox = [0.1f, 0.1f, 0.3f, 0.3f] },
                new UnknownFace { Id = 4, ScreenTimeSeconds = 10, Intervals = [[60, 70]], Embedding = Analysis.EmbeddingCodec.Encode(new float[128]) },
            ],
        };
        var faces = ShareMapping.UnknownFaces(t, "w600k_r50", 600);
        var f = Assert.Single(faces);
        Assert.Equal(3, f.Id);
        Assert.Equal([[10.0, 50.0]], f.Intervals);
        string json = JsonSerializer.Serialize(f);
        Assert.DoesNotContain("thumb", json, StringComparison.OrdinalIgnoreCase);

        // float16 keeps the face: cosine with the original stays above 0.999.
        var back = FaceSignature.Decode(f.Embedding)!;
        var orig = Face(1);
        Assert.True(back.Zip(orig, (a, b) => a * b).Sum() > 0.999);

        Assert.Empty(ShareMapping.UnknownFaces(t, "some_other_model", 600));
    }
}

public class InviteAndChallengeTests
{
    [Fact]
    public void AnInviteRoundTripsAndRejectsGarbage()
    {
        var invite = new Invite(Invite.NewCode(), "abc.access", "secret");
        Assert.Equal(invite, Invite.TryParse(invite.Format()));
        Assert.Null(Invite.TryParse("hello"));
        Assert.Null(Invite.TryParse("xray1.!!!"));
    }

    private static uint[] Random(int items, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, items).Select(_ => (uint)rng.NextInt64(0, 1L << 32)).ToArray();
    }

    [Fact]
    public void TheChallengeAsksOnlyForUnpublishedAnchorsAndProvesPossession()
    {
        var canonical = Random(30_000, 1);
        var all = AnchorSet.Extract(canonical);
        string nonce = PossessionChallenge.NewNonce();
        var picked = PossessionChallenge.Pick(nonce, all.Count, 3);
        Assert.Equal(PossessionChallenge.Positions, picked.Count);
        Assert.All(picked, i => Assert.NotEqual(0, i % 3));
        Assert.Equal(picked, PossessionChallenge.Pick(nonce, all.Count, 3));

        // The contributor's file has 12.3 s more in front.
        int lead = (int)Math.Round(12.3 / FingerprintMatcher.ItemSeconds);
        var local = Random(lead, 2).Concat(canonical).ToArray();
        string answer = PossessionChallenge.Answer(local, picked, all.EverySeconds, all.Length, t => t + 12.3);
        var (passed, informative) = PossessionChallenge.Verify(all, picked, answer);
        Assert.True(PossessionChallenge.Passes(passed, informative), $"{passed}/{informative}");

        // A wrong fit, or other audio, fails.
        string wrongFit = PossessionChallenge.Answer(local, picked, all.EverySeconds, all.Length, t => t + 15);
        Assert.False(PossessionChallenge.Passes(PossessionChallenge.Verify(all, picked, wrongFit).Passed, informative));
        string otherAudio = PossessionChallenge.Answer(Random(30_000, 3), picked, all.EverySeconds, all.Length, t => t);
        Assert.False(PossessionChallenge.Passes(PossessionChallenge.Verify(all, picked, otherAudio).Passed, informative));
    }

    [Fact]
    public void MappingClipsToTheTrustedRangeAndNeverExtrapolates()
    {
        var local = TimelineMapping.ToLocal([[0, 50], [90, 120], [550, 700]], 1, 10, 20, 600, 580);
        Assert.Equal([[30.0, 60.0], [100.0, 130.0], [560.0, 580.0]], local);
        var canonical = TimelineMapping.ToCanonical([[5, 30]], 1, 10, 0, 600, 600);
        Assert.Equal([[0.0, 20.0]], canonical);
    }
}
