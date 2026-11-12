using System.Net;
using Jellyfin.Plugin.SceneXray.Sharing;
using static Xray.Share.Server.Tests.Synth;

namespace Xray.Share.Server.Tests;

/// <summary>Phase 3: leaving, withdrawal, updates, expiry, inactivity and the k-anonymous lookup.</summary>
public sealed class Phase3Tests : IClassFixture<ShareFixture>
{
    private readonly ShareFixture _f;

    public Phase3Tests(ShareFixture f) => _f = f;

    private uint[] Title(int tmdb)
    {
        _f.Credits.Cast[Key(tmdb)] = [100, 101, 102, 103];
        return Fingerprint(3600, tmdb);
    }

    private static List<SharedPerson> Truth() =>
        Enumerable.Range(0, 4).Select(k => Person(100 + k, [(k * 900) + 10, (k * 900) + 890])).ToList();

    private async Task<SubmissionResult> ContributeAsync(ShareClient c, int tmdb, uint[] fp, double lead, int seed, params SharedUnknownFace[] unknowns)
    {
        await _f.DrainAsync();
        var local = lead == 0 ? fp : Release(fp, lead, seed);
        var contribution = Movie(tmdb, local, Truth(), shift: lead) with { UnknownFaces = unknowns };
        var variants = await c.AvailabilityAsync(Key(tmdb));
        var match = variants.Count == 0 ? null : await VariantMatcher.FindAsync(c.ReferenceAsync, variants, [0], (_, _, _) => Task.FromResult(local), Runtime(local));
        return await c.SubmitAsync(new SubmissionRequest(contribution, match?.Variant.VariantId, match?.ToSubmissionFit(), match?.Answer()));
    }

    private static SharedUnknownFace Face(int seed)
    {
        var rng = new Random(seed);
        var v = Enumerable.Range(0, 512).Select(_ => (float)(rng.NextDouble() - 0.5)).ToArray();
        return new SharedUnknownFace(9, "w600k_r50", FaceSignature.Encode(v)!, 300, [[100, 400]]);
    }

    private async Task<Guid> InstallIdAsync(SubmissionResult r) => await _f.ScalarAsync<Guid>("select install_id from submissions where id = @Id", new { Id = r.SubmissionId });

    [Fact]
    public async Task LeavingKeepsTheVotesAnonymousAndDeletesEverythingElse()
    {
        var fp = Title(1001);
        var (a, b) = (await _f.MemberAsync(), await _f.MemberAsync());
        var ra = await ContributeAsync(a, 1001, fp, 0, 0, Face(1));
        await ContributeAsync(b, 1001, fp, 15, 1);
        await _f.AgeInstallsAsync(TimeSpan.FromDays(8));
        await _f.DrainAsync();
        Guid aid = await InstallIdAsync(ra);
        Assert.Equal(1, await _f.ScalarAsync<int>("select count(*) from unknown_faces where install_id = @aid", new { aid }));

        await a.LeaveAsync(withdraw: false);
        await _f.DrainAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<ShareException>(() => a.MeAsync())).Status);
        foreach (string table in new[] { "ledger", "holdings", "challenges", "unknown_faces" })
        {
            Assert.Equal(0, await _f.ScalarAsync<int>($"select count(*) from {table} where install_id = @aid", new { aid }));
        }

        // The submission stays, ownerless, with the trust it had, and still counts.
        Assert.Null(await _f.ScalarAsync<Guid?>("select install_id from submissions where id = @Id", new { Id = ra.SubmissionId }));
        Assert.True(await _f.ScalarAsync<double>("select anon_trust from submissions where id = @Id", new { Id = ra.SubmissionId }) > 0.5);
        Assert.Equal(0, await _f.ScalarAsync<int>("select count(*) from unknown_faces where submission_id = @Id", new { Id = ra.SubmissionId }));
        var v = (await b.AvailabilityAsync(Key(1001))).Single();
        Assert.Equal(ConsensusTimeline.Confirmed, v.Tier);
    }

    [Fact]
    public async Task WithdrawingDeletesTheContributionsAndRecomputes()
    {
        var fp = Title(1002);
        var (a, b) = (await _f.MemberAsync(), await _f.MemberAsync());
        await ContributeAsync(a, 1002, fp, 0, 0);
        var rb = await ContributeAsync(b, 1002, fp, 15, 1);
        await _f.AgeInstallsAsync(TimeSpan.FromDays(8));
        await _f.DrainAsync();
        Assert.Equal(ConsensusTimeline.Confirmed, (await a.AvailabilityAsync(Key(1002))).Single().Tier);

        await b.LeaveAsync(withdraw: true);
        await _f.DrainAsync();
        Assert.Equal(0, await _f.ScalarAsync<int>("select count(*) from submissions where id = @Id", new { Id = rb.SubmissionId }));
        var v = (await a.AvailabilityAsync(Key(1002))).Single();
        Assert.Equal(ConsensusTimeline.Unconfirmed, v.Tier);
    }

    [Fact]
    public async Task RejoiningUsesUpTheInvite()
    {
        var invite = await _f.InviteAsync("rejoin", maxInstalls: 2);
        await (await _f.MemberAsync(invite)).LeaveAsync(false);
        await (await _f.MemberAsync(invite)).LeaveAsync(false);
        Assert.Equal("invite-used", (await Assert.ThrowsAsync<ShareException>(() => _f.MemberAsync(invite))).Error);
    }

    [Fact]
    public async Task TheUpdatesFeedNamesVariantsWhoseConsensusMoved()
    {
        var fp = Title(1003);
        await ContributeAsync(await _f.MemberAsync(), 1003, fp, 0, 0);
        await _f.DrainAsync();
        var receiver = await _f.MemberAsync();
        var v = (await receiver.AvailabilityAsync(Key(1003))).Single();
        var first = await receiver.FetchAsync(v.VariantId);
        Assert.Empty(await receiver.UpdatesAsync(first.Revision));

        await ContributeAsync(await _f.MemberAsync(), 1003, fp, 30, 2);
        await _f.AgeInstallsAsync(TimeSpan.FromDays(8));
        await _f.DrainAsync();
        var moved = Assert.Single(await receiver.UpdatesAsync(first.Revision));
        Assert.Equal(v.VariantId, moved.VariantId);
        Assert.Equal(ConsensusTimeline.Confirmed, moved.Tier);
        Assert.Empty(await receiver.UpdatesAsync(moved.Revision));

        // Refreshing a held title is free.
        int before = (await receiver.MeAsync()).Balance;
        Assert.Equal(moved.Revision, (await receiver.FetchAsync(v.VariantId)).Revision);
        Assert.Equal(before, (await receiver.MeAsync()).Balance);
    }

    [Fact]
    public async Task CreditOlderThanAYearExpiresOldestFirst()
    {
        var fp = Title(1004);
        await ContributeAsync(await _f.MemberAsync(), 1004, fp, 0, 0);
        await _f.DrainAsync();
        var me = await _f.MemberAsync();
        await me.FetchAsync((await me.AvailabilityAsync(Key(1004))).Single().VariantId);
        Assert.Equal(540, (await me.MeAsync()).Balance);
        Guid id = await _f.ScalarAsync<Guid>("select install_id from ledger where kind = 'fetch' order by id desc limit 1");

        // The grace allowance is now 13 months old; the 60 minutes spent came out of it.
        await _f.ExecAsync("update ledger set at = now() - interval '13 months' where install_id = @id and kind = 'grace'", new { id });
        var lifecycle = _f.Factory.Services.GetRequiredService<LifecycleService>();
        await lifecycle.ExpireCreditAsync(default);
        Assert.Equal(0, (await me.MeAsync()).Balance);
        await lifecycle.ExpireCreditAsync(default);
        Assert.Equal(-540, await _f.ScalarAsync<int>("select sum(minutes) from ledger where install_id = @id and kind = 'expiry'", new { id }));
    }

    [Fact]
    public async Task HeldCreditUndecidedForAYearIsCancelled()
    {
        var fp = Title(1005);
        var me = await _f.MemberAsync();
        var r = await ContributeAsync(me, 1005, fp, 0, 0);
        Assert.Equal(60, (await me.MeAsync()).Held);
        await _f.ExecAsync("update submissions set created_at = now() - interval '13 months' where id = @Id", new { Id = r.SubmissionId });
        await _f.Factory.Services.GetRequiredService<LifecycleService>().ExpireCreditAsync(default);
        Assert.Equal(0, (await me.MeAsync()).Held);
    }

    [Fact]
    public async Task AnInstallSilentForAYearIsDeleted()
    {
        var fp = Title(1006);
        var me = await _f.MemberAsync();
        var r = await ContributeAsync(me, 1006, fp, 0, 0);
        Guid id = await InstallIdAsync(r);
        await _f.ExecAsync("update installs set last_seen_at = now() - interval '13 months' where id = @id", new { id });
        await _f.Factory.Services.GetRequiredService<MaintenanceService>().RunAsync(default);
        Assert.Equal(0, await _f.ScalarAsync<int>("select count(*) from installs where id = @id", new { id }));
        Assert.Equal(1, await _f.ScalarAsync<int>("select count(*) from submissions where id = @Id and install_id is null", new { Id = r.SubmissionId }));
    }

    [Fact]
    public async Task AvailabilityAnswersABucketNotATitle()
    {
        var fp = Title(1007);
        var me = await _f.MemberAsync();
        await ContributeAsync(me, 1007, fp, 0, 0);
        await _f.DrainAsync();

        var http = _f.Factory.CreateClient();
        var reg = await _f.Client(await _f.InviteAsync()).RegisterAsync("1.3.1.0");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", reg.Token);
        string prefix = TitleKey.HashPrefix(Key(1007));
        var bucket = await System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<List<VariantSummary>>(http, "/v1/availability?prefix=" + prefix);
        Assert.Contains(bucket!, v => v.TitleKeyHash == TitleKey.Hash(Key(1007)));
        Assert.All(bucket!, v => Assert.StartsWith(prefix, v.TitleKeyHash));

        // The title key itself is no longer accepted.
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/v1/availability?key=" + Key(1007))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/v1/availability?prefix=zzzz")).StatusCode);
    }
}
