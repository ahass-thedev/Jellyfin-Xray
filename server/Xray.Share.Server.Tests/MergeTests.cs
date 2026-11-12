using Jellyfin.Plugin.SceneXray.Sharing;

namespace Xray.Share.Server.Tests;

/// <summary>The multi-voice merge on its own, with synthetic poisoning (design §7 phase 2).</summary>
public sealed class MergeTests
{
    private const double Runtime = 1200;

    // The truth: 20 scenes of 60 s; person 100 in scenes 0–4, 101 in 5–9, 102 in 10–14, 103 in 15–19.
    private static readonly List<double[]> Scenes = Synth.Scenes(Runtime);

    private static List<SharedPerson> Truth(double shift = 0, Func<int, int>? swap = null) =>
        Enumerable.Range(0, 4).Select(k => new SharedPerson(
            swap?.Invoke(100 + k) ?? 100 + k,
            "face",
            0.8,
            [[(k * 300) + 5 + shift, (k * 300) + 295 + shift]])).ToList();

    private static Ballot B(Guid voice, List<SharedPerson> people, double weight = 0.5, bool voting = true, List<double[]>? scenes = null, List<SharedMusicCue>? music = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), voice, weight, voting, 0, Runtime, new CanonicalTimeline(scenes ?? Scenes, people, [], music ?? []));

    private static MergeResult Run(params Ballot[] ballots) => Merge.Run(Guid.NewGuid(), 2, Runtime, ballots);

    private static HashSet<int> PeopleIn(ConsensusTimeline c, double from, double to) =>
        c.People.Where(p => p.Intervals.Any(i => i[0] < to && i[1] > from)).Select(p => p.TmdbPerson).ToHashSet();

    [Fact]
    public void TwoIndependentVoicesAgreeAndConfirm()
    {
        var r = Run(B(Guid.NewGuid(), Truth()), B(Guid.NewGuid(), Truth(0.4)));
        var c = r.Consensus!;
        Assert.Equal(ConsensusTimeline.Confirmed, c.Tier);
        Assert.Equal(2, c.Voices);
        Assert.Equal(4, c.People.Count);
        Assert.Equal([100], PeopleIn(c, 100, 200));
        Assert.True(c.People[0].Confidence > 0.5);
        Assert.All(r.Scores.Values, s => Assert.True(s.Corroborated));
        Assert.All(r.Scores.Values, s => Assert.False(s.Outlier));
    }

    [Fact]
    public void OneVoiceIsUnconfirmedAndCappedAtHalf()
    {
        var c = Run(B(Guid.NewGuid(), Truth())).Consensus!;
        Assert.Equal(ConsensusTimeline.Unconfirmed, c.Tier);
        Assert.All(c.People, p => Assert.True(p.Confidence <= 0.5));
    }

    [Fact]
    public void FakeCastIsOutvotedAndMarkedAnOutlier()
    {
        // The poisoner owns the file and names real, credited actors in the wrong scenes.
        var poison = B(Guid.NewGuid(), Truth(swap: p => p == 100 ? 103 : p == 103 ? 100 : p == 101 ? 102 : 101));
        var r = Run(B(Guid.NewGuid(), Truth()), B(Guid.NewGuid(), Truth(0.3)), B(Guid.NewGuid(), Truth(-0.3)), poison);
        Assert.Equal([100], PeopleIn(r.Consensus!, 100, 200));
        Assert.Equal([103], PeopleIn(r.Consensus!, 1000, 1100));
        Assert.True(r.Scores[poison.SubmissionId].Outlier);
        Assert.Equal(3, r.Scores.Values.Count(s => s.Corroborated && !s.Outlier));
    }

    [Fact]
    public void ShiftedIntervalsAreOutvoted()
    {
        var poison = B(Guid.NewGuid(), Truth(shift: 300));
        var r = Run(B(Guid.NewGuid(), Truth()), B(Guid.NewGuid(), Truth()), B(Guid.NewGuid(), Truth()), poison);
        Assert.True(r.Scores[poison.SubmissionId].Outlier);
        Assert.Equal([100], PeopleIn(r.Consensus!, 100, 200));
        var p100 = r.Consensus!.People.Single(p => p.TmdbPerson == 100);
        Assert.InRange(p100.Intervals[^1][1], 290, 300);
    }

    [Fact]
    public void SybilInstallsFromOneInviteAreOneVoice()
    {
        // Three installs on one invite push a fake cast; two honest friends disagree.
        var sybil = Guid.NewGuid();
        var fake = Truth(swap: p => 203 - p);
        var ballots = new[] { B(sybil, fake), B(sybil, fake), B(sybil, fake), B(Guid.NewGuid(), Truth()), B(Guid.NewGuid(), Truth()) };
        var r = Run(ballots);
        Assert.Equal(2, r.Consensus!.Voices); // the sybils were dropped as outliers of 3 voices
        Assert.Equal([100], PeopleIn(r.Consensus!, 100, 200));
        Assert.All(ballots.Take(3), b => Assert.True(r.Scores[b.SubmissionId].Outlier));
    }

    [Fact]
    public void InstallsOfOneInviteNeverCorroborateEachOther()
    {
        var friend = Guid.NewGuid();
        var r = Run(B(friend, Truth()), B(friend, Truth()));
        Assert.Equal(ConsensusTimeline.Unconfirmed, r.Consensus!.Tier);
        Assert.All(r.Scores.Values, s => Assert.False(s.Corroborated));
    }

    [Fact]
    public void YoungInstallsDoNotVoteWhileOlderOnesHave()
    {
        var young = B(Guid.NewGuid(), Truth(swap: p => p == 100 ? 101 : p), voting: false);
        var r = Run(B(Guid.NewGuid(), Truth()), young);
        Assert.Equal(ConsensusTimeline.Unconfirmed, r.Consensus!.Tier);
        Assert.Equal(1, r.Consensus.Voices);
        Assert.Equal([100], PeopleIn(r.Consensus!, 100, 200));

        // With nobody voting yet, a new title is still served, unconfirmed.
        var fresh = Run(B(Guid.NewGuid(), Truth(), voting: false), B(Guid.NewGuid(), Truth(), voting: false));
        Assert.Equal(ConsensusTimeline.Unconfirmed, fresh.Consensus!.Tier);
        Assert.Empty(fresh.Scores);
    }

    [Fact]
    public void SceneBoundariesWithinASecondAndAHalfMerge()
    {
        var a = Synth.Scenes(Runtime);
        var b = a.Select(s => new[] { s[0] == 0 ? 0 : s[0] + 1.2, s[1] >= Runtime ? Runtime : s[1] + 1.2 }).ToList();
        var c = Run(B(Guid.NewGuid(), Truth(), scenes: a), B(Guid.NewGuid(), Truth(), scenes: b), B(Guid.NewGuid(), Truth(), scenes: a)).Consensus!;
        Assert.Equal(20, c.Scenes.Count);
        Assert.InRange(c.Scenes[1][0], 60, 61.2);
    }

    [Fact]
    public void MusicNeedsTwoVoicesAgreeingOnSpanAndSong()
    {
        var song = new SharedMusicCue(300, 360, "Clubbed to Death", "Rob D", null, null, "library", 0.9);
        var echo = song with { Start = 301.5, End = 358, Title = "clubbed to death!" };
        var other = song with { Title = "Something else" };
        Assert.Single(Run(B(Guid.NewGuid(), Truth(), music: [song]), B(Guid.NewGuid(), Truth(), music: [echo])).Consensus!.Music);
        Assert.Empty(Run(B(Guid.NewGuid(), Truth(), music: [song]), B(Guid.NewGuid(), Truth())).Consensus!.Music);
        Assert.Empty(Run(B(Guid.NewGuid(), Truth(), music: [song]), B(Guid.NewGuid(), Truth(), music: [other])).Consensus!.Music);
        var sybil = Guid.NewGuid();
        Assert.Empty(Run(B(sybil, Truth(), music: [song]), B(sybil, Truth(), music: [echo])).Consensus!.Music);
    }

    [Fact]
    public void QuarantinedBallotsAreScoredButCarryNoWeight()
    {
        var q = B(Guid.NewGuid(), Truth(swap: p => p == 100 ? 101 : p), weight: 0);
        var r = Run(B(Guid.NewGuid(), Truth()), B(Guid.NewGuid(), Truth()), q);
        Assert.Equal(2, r.Consensus!.Voices);
        Assert.NotNull(r.Scores[q.SubmissionId].Agreement);
        Assert.Equal([100], PeopleIn(r.Consensus!, 100, 200));
    }
}
