using System.Net;
using Jellyfin.Plugin.SceneXray.Sharing;
using static Xray.Share.Server.Tests.Synth;

namespace Xray.Share.Server.Tests;

/// <summary>Phase 2 end to end: consensus, trust, held credit, the possession challenge and versions.</summary>
public sealed class Phase2Tests : IClassFixture<ShareFixture>
{
    private readonly ShareFixture _f;

    public Phase2Tests(ShareFixture f) => _f = f;

    private uint[] Title(int tmdb, double seconds = 3600)
    {
        _f.Credits.Cast[Key(tmdb)] = [100, 101, 102, 103];
        return Fingerprint(seconds, tmdb);
    }

    // Four people, 15 minutes each, in canonical time.
    private static List<SharedPerson> Truth(Func<int, int>? swap = null) =>
        Enumerable.Range(0, 4).Select(k => Person(swap?.Invoke(100 + k) ?? 100 + k, [(k * 900) + 10, (k * 900) + 890])).ToList();

    private static List<SharedPerson> Poison() => Truth(p => 203 - p);

    /// <summary>
    /// Contributes from a file with <paramref name="lead"/> seconds in front, aligning to the
    /// existing variant (and answering its challenge) when there is one.
    /// </summary>
    private async Task<SubmissionResult> ContributeAsync(ShareClient c, int tmdb, uint[] fp, double lead, int seed, List<SharedPerson> people, string version = "2")
    {
        await _f.DrainAsync(); // availability lists variants once their consensus exists
        var local = lead == 0 ? fp : Release(fp, lead, seed);
        var contribution = Movie(tmdb, local, people, version, shift: lead);
        var variants = await c.AvailabilityAsync(Key(tmdb));
        var match = variants.Count == 0 ? null : await VariantMatcher.FindAsync(c.ReferenceAsync, variants, [0], (_, _, _) => Task.FromResult(local), Runtime(local));
        return await c.SubmitAsync(new SubmissionRequest(contribution, match?.Variant.VariantId, match?.ToSubmissionFit(), match?.Answer()));
    }

    private async Task<ShareClient[]> FriendsAsync(int n)
    {
        var list = new ShareClient[n];
        for (int i = 0; i < n; i++)
        {
            list[i] = await _f.MemberAsync();
        }

        return list;
    }

    [Fact]
    public async Task HeldCreditIsReleasedWhenAnIndependentVoiceAgrees()
    {
        var fp = Title(801);
        var (a, b) = (await _f.MemberAsync(), await _f.MemberAsync());
        Assert.Equal(SubmissionResult.Accepted, (await ContributeAsync(a, 801, fp, 0, 0, Truth())).Status);
        await _f.DrainAsync();
        Assert.Equal(SubmissionResult.Accepted, (await ContributeAsync(b, 801, fp, 20, 1, Truth())).Status);
        await _f.AgeInstallsAsync(TimeSpan.FromDays(8));
        await _f.DrainAsync();

        var v = Assert.Single(await a.AvailabilityAsync(Key(801)));
        Assert.Equal(ConsensusTimeline.Confirmed, v.Tier);
        foreach (var me in new[] { a, b })
        {
            var s = await me.MeAsync();
            Assert.Equal(0, s.Held);
            Assert.Equal(720, s.Balance); // 600 grace + 60 earned + 60 released, now corroborated
            Assert.True(s.Trust > 0.5);
        }

        var c = await (await _f.MemberAsync()).FetchAsync(v.VariantId);
        Assert.Equal(2, c.Voices);
        Assert.True(c.People.Max(p => p.Confidence) > 0.5);
    }

    [Fact]
    public async Task APoisonerIsOutvotedLosesTheHeldCreditAndIsQuarantined()
    {
        var poisoner = await _f.MemberAsync();
        foreach (int tmdb in new[] { 802, 803 })
        {
            var fp = Title(tmdb);
            var honest = await FriendsAsync(3);
            await ContributeAsync(honest[0], tmdb, fp, 0, 0, Truth());
            await ContributeAsync(honest[1], tmdb, fp, 15, 2, Truth());
            await ContributeAsync(honest[2], tmdb, fp, 30, 3, Truth());
            Assert.Equal(SubmissionResult.Accepted, (await ContributeAsync(poisoner, tmdb, fp, 5, 4, Poison())).Status);
        }

        await _f.AgeInstallsAsync(TimeSpan.FromDays(8));
        await _f.DrainAsync();

        var status = await poisoner.MeAsync();
        Assert.Equal("quarantined", status.State);
        Assert.Equal(0, status.Held);
        Assert.True(status.Trust < 0.3);
        Assert.Equal(2, await _f.ScalarAsync<int>("select count(*) from submissions where outlier and credit_state = 'clawed'"));

        // The consensus is the honest one.
        var v = (await poisoner.AvailabilityAsync(Key(802))).Single();
        var c = await (await _f.MemberAsync()).FetchAsync(v.VariantId);
        Assert.Equal(3, c.Voices);
        Assert.Equal(100, c.People.Single(p => p.Intervals[0][0] < 100).TmdbPerson);

        // Quarantine is all-or-nothing: no fetching either.
        var ex = await Assert.ThrowsAsync<ShareException>(() => poisoner.FetchAsync(v.VariantId));
        Assert.Equal(HttpStatusCode.Forbidden, ex.Status);

        // A quarantined install's new submissions are stored, earn nothing and carry no weight.
        var fp3 = Title(804);
        var q = await ContributeAsync(poisoner, 804, fp3, 0, 0, Truth());
        Assert.Equal(0, q.Credit + q.Held);
        Assert.Equal("quarantined", await _f.ScalarAsync<string>("select status from submissions where id = @Id", new { Id = q.SubmissionId }));
    }

    [Fact]
    public async Task ThreeInstallsOnOneInviteCannotOutvoteTwoFriends()
    {
        var fp = Title(805);
        var invite = await _f.InviteAsync("sybil");
        var sybils = new[] { await _f.MemberAsync(invite), await _f.MemberAsync(invite), await _f.MemberAsync(invite) };
        var honest = await FriendsAsync(2);
        await ContributeAsync(sybils[0], 805, fp, 0, 0, Poison());
        await ContributeAsync(sybils[1], 805, fp, 10, 5, Poison());
        await ContributeAsync(sybils[2], 805, fp, 20, 6, Poison());
        await ContributeAsync(honest[0], 805, fp, 30, 7, Truth());
        await ContributeAsync(honest[1], 805, fp, 40, 8, Truth());
        await _f.AgeInstallsAsync(TimeSpan.FromDays(8));
        await _f.DrainAsync();

        var v = (await honest[0].AvailabilityAsync(Key(805))).Single();
        var c = await honest[0].FetchAsync(v.VariantId);
        Assert.Equal(ConsensusTimeline.Confirmed, c.Tier);
        Assert.Equal(100, c.People.Single(p => p.Intervals[0][0] < 100).TmdbPerson);
        Assert.Equal(3, await _f.ScalarAsync<int>("select count(*) from submissions s join installs i on i.id = s.install_id join invites v on v.id = i.invite_id where v.note = 'sybil' and s.outlier"));
    }

    [Fact]
    public async Task InstallsVoteOnlyAfterSevenDays()
    {
        var fp = Title(806);
        var (a, b) = (await _f.MemberAsync(), await _f.MemberAsync());
        await ContributeAsync(a, 806, fp, 0, 0, Truth());
        await ContributeAsync(b, 806, fp, 12, 9, Truth());
        await _f.DrainAsync();
        Assert.Equal(ConsensusTimeline.Unconfirmed, (await a.AvailabilityAsync(Key(806))).Single().Tier);

        // The hourly maintenance notices installs that have just turned seven days old.
        await _f.AgeInstallsAsync(TimeSpan.FromDays(7) + TimeSpan.FromMinutes(30));
        await _f.Factory.Services.GetRequiredService<MaintenanceService>().RunAsync(default);
        await _f.DrainAsync();
        Assert.Equal(ConsensusTimeline.Confirmed, (await a.AvailabilityAsync(Key(806))).Single().Tier);
    }

    [Fact]
    public async Task TheChallengeMustBeAnsweredFromTheAudio()
    {
        var fp = Title(807);
        await ContributeAsync(await _f.MemberAsync(), 807, fp, 0, 0, Truth());
        await _f.DrainAsync();
        var me = await _f.MemberAsync();
        var local = Release(fp, 25, 10);
        var variants = await me.AvailabilityAsync(Key(807));
        var match = await VariantMatcher.FindAsync(me.ReferenceAsync, variants, [0], (_, _, _) => Task.FromResult(local), Runtime(local));
        var c = Movie(807, local, Truth(), shift: 25);
        var vid = match!.Variant.VariantId;

        var none = await me.SubmitAsync(new SubmissionRequest(c, vid, match.ToSubmissionFit(), null));
        Assert.Equal(SubmissionResult.Rejected, none.Status);

        // Right fit, wrong audio: the anchors are copied from nowhere.
        var fake = new ChallengeAnswer(match.Reference.Nonce, PossessionChallenge.Answer(Fingerprint(3600, 999), PossessionChallenge.Pick(match.Reference.Nonce, match.Reference.Count, match.Reference.Stride), 20, 24, t => t + 25));
        var wrong = await me.SubmitAsync(new SubmissionRequest(c, vid, match.ToSubmissionFit(), fake));
        Assert.Equal(SubmissionResult.Rejected, wrong.Status);
        Assert.Contains("possession", wrong.Reason);

        // The nonce is spent by the failed try; a fresh reference gives a new one.
        Assert.Equal(SubmissionResult.Rejected, (await me.SubmitAsync(new SubmissionRequest(c, vid, match.ToSubmissionFit(), match.Answer()))).Status);
        match = await VariantMatcher.FindAsync(me.ReferenceAsync, variants, [0], (_, _, _) => Task.FromResult(local), Runtime(local));
        Assert.Equal(SubmissionResult.Accepted, (await me.SubmitAsync(new SubmissionRequest(c, vid, match!.ToSubmissionFit(), match.Answer()))).Status);
    }

    [Fact]
    public async Task ANewerAnalyserTakesOverOnlyOnceConfirmedAndTheOldOneGoesAfterNinetyDays()
    {
        var fp = Title(808);
        var (a, b) = (await _f.MemberAsync(), await _f.MemberAsync());
        await ContributeAsync(a, 808, fp, 0, 0, Truth());
        await ContributeAsync(b, 808, fp, 9, 11, Truth());
        await _f.AgeInstallsAsync(TimeSpan.FromDays(8));
        await _f.DrainAsync();
        Assert.Equal((2, ConsensusTimeline.Confirmed), (await a.AvailabilityAsync(Key(808))).Select(v => (v.Version, v.Tier)).Single());

        // One v3 voice: v2 stays served.
        await ContributeAsync(a, 808, fp, 0, 0, Truth(), version: "3");
        await _f.DrainAsync();
        Assert.Equal(2, (await a.AvailabilityAsync(Key(808))).Single().Version);

        // A second v3 voice confirms it, and it takes over.
        await ContributeAsync(b, 808, fp, 9, 11, Truth(), version: "3");
        await _f.DrainAsync();
        Assert.Equal((3, ConsensusTimeline.Confirmed), (await a.AvailabilityAsync(Key(808))).Select(v => (v.Version, v.Tier)).Single());

        var variant = (await a.AvailabilityAsync(Key(808))).Single().VariantId;
        await _f.Factory.Services.GetRequiredService<MaintenanceService>().RunAsync(default);
        Assert.Equal(2, await _f.ScalarAsync<int>("select count(*) from submissions where variant_id = @variant and version = 2", new { variant }));
        await _f.ExecAsync("update consensus set confirmed_at = now() - interval '91 days' where variant_id = @variant and version = 3", new { variant });
        await _f.Factory.Services.GetRequiredService<MaintenanceService>().RunAsync(default);
        Assert.Equal(0, await _f.ScalarAsync<int>("select count(*) from submissions where variant_id = @variant and version = 2", new { variant }));
        Assert.Equal(0, await _f.ScalarAsync<int>("select count(*) from consensus where variant_id = @variant and version = 2", new { variant }));
    }

    [Fact]
    public async Task MusicAgreedByTwoFriendsIsServed()
    {
        var fp = Title(809);
        var song = new SharedMusicCue(300, 360, "Clubbed to Death", "Rob D", null, null, "library", 0.9);
        var (a, b) = (await _f.MemberAsync(), await _f.MemberAsync());
        var local = fp;
        await a.SubmitAsync(new SubmissionRequest(Movie(809, fp, Truth(), music: [song]), null, null, null));
        await _f.DrainAsync();
        var shifted = Release(fp, 10, 12);
        var variants = await b.AvailabilityAsync(Key(809));
        var match = await VariantMatcher.FindAsync(b.ReferenceAsync, variants, [0], (_, _, _) => Task.FromResult(shifted), Runtime(shifted));
        await b.SubmitAsync(new SubmissionRequest(Movie(809, shifted, Truth(), music: [song with { Title = "Clubbed To Death" }], shift: 10), match!.Variant.VariantId, match.ToSubmissionFit(), match.Answer()));
        await _f.AgeInstallsAsync(TimeSpan.FromDays(8));
        await _f.DrainAsync();

        var cue = Assert.Single((await a.FetchAsync(variants[0].VariantId)).Music);
        Assert.InRange(cue.Start, 299.5, 300.5);
    }
}
