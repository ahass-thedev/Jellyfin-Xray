using System.Net;
using System.Net.Http.Json;
using Jellyfin.Plugin.SceneXray.Sharing;
using static Xray.Share.Server.Tests.Synth;

namespace Xray.Share.Server.Tests;

/// <summary>Phase 1: register, submit, availability, reference, fetch and credits, end to end.</summary>
public sealed class Phase1Tests : IClassFixture<ShareFixture>
{
    private readonly ShareFixture _f;

    public Phase1Tests(ShareFixture f) => _f = f;

    private (int Tmdb, uint[] Fp) Title(int tmdb, double seconds = 3600, params int[] cast)
    {
        _f.Credits.Cast[Key(tmdb)] = [.. cast.Length == 0 ? [100, 101, 102] : cast];
        return (tmdb, Fingerprint(seconds, tmdb));
    }

    [Fact]
    public async Task HealthReportsTheDatabaseAndMinimumVersions()
    {
        var h = await _f.Client().HealthAsync();
        Assert.Equal("ok", h.Status);
        Assert.True(h.Db);
        Assert.Equal(2, h.MinAnalyzerVersion);
    }

    [Fact]
    public async Task AnInviteRegistersAtMostThreeInstallsWithTheGraceAllowance()
    {
        var invite = await _f.InviteAsync("three");
        for (int i = 0; i < 3; i++)
        {
            var r = await _f.Client(invite).RegisterAsync("1.3.1.0");
            Assert.Equal(600, r.Balance);
            Assert.Equal(600, (await _f.Client(invite, r.Token).MeAsync()).Balance);
        }

        var fourth = await Assert.ThrowsAsync<ShareException>(() => _f.Client(invite).RegisterAsync("1.3.1.0"));
        Assert.Equal("invite-used", fourth.Error);

        var bogus = await Assert.ThrowsAsync<ShareException>(() => _f.Client(new Invite("nope", "", "")).RegisterAsync("1.3.1.0"));
        Assert.Equal("invite", bogus.Error);
    }

    [Fact]
    public async Task ARevokedInviteRegistersNothing()
    {
        var invite = await _f.InviteAsync("revoked");
        Guid id = await _f.ScalarAsync<Guid>("select id from invites where note = 'revoked'");
        var o = ShareApp.Options(_f.Factory.Services.GetRequiredService<IConfiguration>());
        Assert.Equal(0, await Cli.RunAsync(["invite", "revoke", id.ToString()], _f.Db, o, new StringWriter(), new StringReader(string.Empty)));
        await Assert.ThrowsAsync<ShareException>(() => _f.Client(invite).RegisterAsync("1.3.1.0"));
    }

    [Fact]
    public async Task TheInviteCarriesTheAccessTokenButTheServerKeepsOnlyItsId()
    {
        var o = ShareApp.Options(_f.Factory.Services.GetRequiredService<IConfiguration>());
        var output = new StringWriter();
        Assert.Equal(0, await Cli.RunAsync(["invite", "create", "--note", "cf", "--access-client-id", "abc.access"], _f.Db, o, output, new StringReader("s3cret\n")));
        var invite = Invite.TryParse(output.ToString().Split('\n')[1]);
        Assert.Equal("abc.access", invite!.AccessClientId);
        Assert.Equal("s3cret", invite.AccessClientSecret);
        Assert.Equal("abc.access", await _f.ScalarAsync<string>("select access_client_id from invites where note = 'cf'"));
        Assert.Equal(0, await _f.ScalarAsync<int>("select count(*) from invites where note = 'cf' and (code_hash::text like '%s3cret%' or access_client_id like '%s3cret%')"));
    }

    [Fact]
    public async Task EverythingButRegisterAndHealthNeedsAToken()
    {
        var anon = _f.Client(await _f.InviteAsync());
        var ex = await Assert.ThrowsAsync<ShareException>(() => anon.AvailabilityAsync(Key(1)));
        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Assert.ThrowsAsync<ShareException>(() => _f.Client(null, "forged").MeAsync())).Status);
    }

    [Fact]
    public async Task AContributionStartsAVariantAndEarnsHalfNowHalfHeld()
    {
        var (tmdb, fp) = Title(603);
        var me = await _f.MemberAsync();
        var r = await me.SubmitAsync(new SubmissionRequest(Movie(tmdb, fp, [Person(100, [60, 300])]), null, null, null));

        Assert.Equal(SubmissionResult.Accepted, r.Status);
        Assert.True(r.NewVariant);
        Assert.Equal(60, r.Credit); // 60 minutes × E=2, half now
        Assert.Equal(60, r.Held);

        // Nothing is corroborated yet, so the spendable half does not count (cap 2 × 0).
        var status = await me.MeAsync();
        Assert.Equal(600, status.Balance);
        Assert.Equal(60, status.Held);
        Assert.Equal(1, status.Contributed);
    }

    [Fact]
    public async Task AvailabilityReferenceFitAndFetchFromAnotherRelease()
    {
        var (tmdb, fp) = Title(604);
        var contributor = await _f.MemberAsync();
        Assert.Equal(SubmissionResult.Accepted, (await contributor.SubmitAsync(new SubmissionRequest(Movie(tmdb, fp, [Person(100, [60, 300]), Person(101, [400, 900])]), null, null, null))).Status);
        await _f.DrainAsync();

        var receiver = await _f.MemberAsync();
        var variants = await receiver.AvailabilityAsync(Key(tmdb));
        var v = Assert.Single(variants);
        Assert.Equal(ConsensusTimeline.Unconfirmed, v.Tier);
        Assert.Equal(2, v.Version);

        // The receiver's file has a 25 s logo in front: the fit finds it, nothing is charged yet.
        var local = Release(fp, 25, 7);
        var match = await VariantMatcher.FindAsync(receiver.ReferenceAsync, variants, [0], (_, _, _) => Task.FromResult(local), Runtime(local));
        Assert.NotNull(match);
        Assert.InRange(match!.Fit.Offset, 24.8, 25.2);
        Assert.Equal(600, (await receiver.MeAsync()).Balance);

        var c = await receiver.FetchAsync(v.VariantId);
        Assert.Equal(ConsensusTimeline.Unconfirmed, c.Tier);
        Assert.True(c.Revision > 0);
        Assert.All(c.People, p => Assert.True(p.Confidence <= 0.5));
        Assert.Equal([60.0, 300.0], c.People.Single(p => p.TmdbPerson == 100).Intervals[0]);
        Assert.Equal(540, (await receiver.MeAsync()).Balance);

        // A second fetch of a held variant is free.
        await receiver.FetchAsync(v.VariantId);
        Assert.Equal(540, (await receiver.MeAsync()).Balance);
        Assert.Equal(1, (await receiver.MeAsync()).Received);

        // The contributor's own title costs it nothing.
        await contributor.FetchAsync(v.VariantId);
        Assert.Equal(600, (await contributor.MeAsync()).Balance);
    }

    [Fact]
    public async Task FetchingStopsWhenCreditRunsOut()
    {
        var (tmdb, fp) = Title(605, 3 * 3600);
        await (await _f.MemberAsync()).SubmitAsync(new SubmissionRequest(Movie(tmdb, fp), null, null, null));
        var (tmdb2, fp2) = Title(606, 3 * 3600);
        await (await _f.MemberAsync()).SubmitAsync(new SubmissionRequest(Movie(tmdb2, fp2), null, null, null));
        var (tmdb3, fp3) = Title(607, 5 * 3600);
        await (await _f.MemberAsync()).SubmitAsync(new SubmissionRequest(Movie(tmdb3, fp3), null, null, null));
        await _f.DrainAsync();

        var receiver = await _f.MemberAsync();
        var v1 = (await receiver.AvailabilityAsync(Key(tmdb))).Single();
        var v2 = (await receiver.AvailabilityAsync(Key(tmdb2))).Single();
        var v3 = (await receiver.AvailabilityAsync(Key(tmdb3))).Single();
        await receiver.FetchAsync(v1.VariantId);
        await receiver.FetchAsync(v2.VariantId);
        Assert.Equal(240, (await receiver.MeAsync()).Balance);
        var ex = await Assert.ThrowsAsync<ShareException>(() => receiver.FetchAsync(v3.VariantId));
        Assert.Equal(HttpStatusCode.PaymentRequired, ex.Status);
        Assert.Equal("credit", ex.Error);
        Assert.Equal(240, (await receiver.MeAsync()).Balance);
    }

    [Fact]
    public async Task ACopyBackIsStoredAsDerivedWithNoCredit()
    {
        var (tmdb, fp) = Title(608);
        await (await _f.MemberAsync()).SubmitAsync(new SubmissionRequest(Movie(tmdb, fp, [Person(100, [60, 300])]), null, null, null));
        await _f.DrainAsync();

        var copier = await _f.MemberAsync();
        var v = (await copier.AvailabilityAsync(Key(tmdb))).Single();
        await copier.FetchAsync(v.VariantId);

        // Resubmitted as a new variant, or to the same one: derived either way.
        var asNew = await copier.SubmitAsync(new SubmissionRequest(Movie(tmdb, fp, [Person(100, [60, 300])]), null, null, null));
        Assert.Equal(SubmissionResult.Derived, asNew.Status);
        Assert.Equal(0, asNew.Credit + asNew.Held);
        Assert.Equal(0, (await copier.MeAsync()).Held);
    }

    [Fact]
    public async Task AResubmissionReplacesTheOldOneAndOnlyANewerAnalyserEarnsAgain()
    {
        var (tmdb, fp) = Title(609);
        var me = await _f.MemberAsync();
        var first = await me.SubmitAsync(new SubmissionRequest(Movie(tmdb, fp, [Person(100, [60, 300])]), null, null, null));
        var local = await VariantMatcher.FindAsync(me.ReferenceAsync, [new VariantSummary(first.VariantId!.Value, "", "theatrical", Runtime(fp), "unconfirmed", 2, 0)], [0], (_, _, _) => Task.FromResult(fp), Runtime(fp));

        var again = await me.SubmitAsync(new SubmissionRequest(Movie(tmdb, fp, [Person(101, [60, 300])]), first.VariantId, local!.ToSubmissionFit(), local.Answer()));
        Assert.Equal(SubmissionResult.Accepted, again.Status);
        Assert.False(again.NewVariant);
        Assert.Equal(0, again.Credit);
        Assert.Equal(60, (await me.MeAsync()).Held);
        Assert.Equal(1, await _f.ScalarAsync<int>("select count(*) from submissions where variant_id = @v and status = 'live'", new { v = first.VariantId }));

        // A nonce is single-use: the next submission answers a fresh one.
        Assert.Equal(SubmissionResult.Rejected, (await me.SubmitAsync(new SubmissionRequest(Movie(tmdb, fp, [Person(101, [60, 300])], version: "3"), first.VariantId, local.ToSubmissionFit(), local.Answer()))).Status);
        local = await VariantMatcher.FindAsync(me.ReferenceAsync, [local.Variant], [0], (_, _, _) => Task.FromResult(fp), Runtime(fp));
        var newer = await me.SubmitAsync(new SubmissionRequest(Movie(tmdb, fp, [Person(101, [60, 300])], version: "3"), first.VariantId, local!.ToSubmissionFit(), local.Answer()));
        Assert.Equal(60, newer.Credit);
        Assert.Equal(120, (await me.MeAsync()).Held);
    }

    [Fact]
    public async Task AContributionToAnExistingVariantIsMappedIntoCanonicalTime()
    {
        var (tmdb, fp) = Title(610);
        var first = await (await _f.MemberAsync()).SubmitAsync(new SubmissionRequest(Movie(tmdb, fp), null, null, null));
        await _f.DrainAsync();

        // A file with a 30 s logo: its person at local 90–330 s is canonical 60–300 s.
        var local = Release(fp, 30, 11);
        var second = await _f.MemberAsync();
        var variants = await second.AvailabilityAsync(Key(tmdb));
        var match = await VariantMatcher.FindAsync(second.ReferenceAsync, variants, [0], (_, _, _) => Task.FromResult(local), Runtime(local));
        var r = await second.SubmitAsync(new SubmissionRequest(Movie(tmdb, local, [Person(102, [90, 330])]), match!.Variant.VariantId, match.ToSubmissionFit(), match.Answer()));
        Assert.Equal(SubmissionResult.Accepted, r.Status);
        Assert.Equal(first.VariantId, r.VariantId);
        await _f.DrainAsync();

        var c = await second.FetchAsync(first.VariantId!.Value);
        var p = Assert.Single(c.People);
        Assert.Equal(102, p.TmdbPerson);
        Assert.InRange(p.Intervals[0][0], 59.8, 60.2);
        Assert.InRange(p.Intervals[0][1], 299.8, 300.2);
    }

    [Theory]
    [InlineData("stranger")]
    [InlineData("outside")]
    [InlineData("overlap")]
    [InlineData("old-analyzer")]
    [InlineData("runtime-lie")]
    [InlineData("unknown-faces")]
    [InlineData("bad-mbid")]
    [InlineData("no-id")]
    public async Task StructuralChecksRejectWithAReason(string kind)
    {
        int tmdb = 700 + kind.Length + kind[0];
        var (_, fp) = Title(tmdb);
        var c = Movie(tmdb, fp, [Person(100, [60, 300])]);
        c = kind switch
        {
            "stranger" => c with { People = [Person(999, [60, 300])] },
            "outside" => c with { People = [Person(100, [60, 99999])] },
            "overlap" => c with { People = [Person(100, [60, 300], [200, 400])] },
            "old-analyzer" => c with { Analyzer = c.Analyzer with { Version = "1" } },
            "runtime-lie" => c with { Title = c.Title with { Runtime = c.Title.Runtime * 2 } },
            "unknown-faces" => c with { UnknownFaces = [new SharedUnknownFace(1, "w600k_r50", "AAAA", 30, [[1, 31]])] },
            "bad-mbid" => c with { Music = [new SharedMusicCue(10, 40, "Song", null, null, "not-a-uuid", "library", 0.9)] },
            "no-id" => c with { Title = c.Title with { Tmdb = null } },
            _ => c,
        };

        var r = await (await _f.MemberAsync()).SubmitAsync(new SubmissionRequest(c, null, null, null));
        Assert.Equal(SubmissionResult.Rejected, r.Status);
        Assert.False(string.IsNullOrEmpty(r.Reason));
        Assert.Equal(0, await _f.ScalarAsync<int>("select count(*) from titles where key = @k", new { k = Key(tmdb) }));
    }

    [Fact]
    public async Task UnknownFieldsAreRejectedBeforeAnythingIsParsed()
    {
        var http = _f.Factory.CreateClient();
        var reg = await _f.Client(await _f.InviteAsync()).RegisterAsync("1.3.1.0");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", reg.Token);
        var resp = await http.PostAsync("/v1/submissions", new StringContent("""{"contribution":null,"sneaky":1}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
    }

    [Fact]
    public async Task AnUnreachableTmdbAsksForARetryInsteadOfRejecting()
    {
        var (tmdb, fp) = Title(611);
        var me = await _f.MemberAsync();
        _f.Credits.Down = true;
        try
        {
            var ex = await Assert.ThrowsAsync<ShareException>(() => me.SubmitAsync(new SubmissionRequest(Movie(tmdb, fp), null, null, null)));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.Status);
            Assert.NotNull(ex.RetryAfter);
        }
        finally
        {
            _f.Credits.Down = false;
        }
    }

    [Fact]
    public async Task SingleVoiceMusicIsNeverServed()
    {
        var (tmdb, fp) = Title(612);
        var me = await _f.MemberAsync();
        var r = await me.SubmitAsync(new SubmissionRequest(Movie(tmdb, fp, music: [new SharedMusicCue(100, 160, "Free text", "Anyone", null, null, "audd", 0.9)]), null, null, null));
        await _f.DrainAsync();
        Assert.Empty((await me.FetchAsync(r.VariantId!.Value)).Music);
    }

    [Fact]
    public async Task TheClientSaysWhatItIs()
    {
        // Cloudflare's zone rules block an empty User-Agent.
        Assert.StartsWith("XRay-Jellyfin/", ShareClient.UserAgent);
        Assert.Equal("ok", (await _f.Client().HealthAsync()).Status);
    }

    [Fact]
    public async Task OnlyV1RoutesExist()
    {
        var resp = await _f.Factory.CreateClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}

/// <summary>Rate limits are on in production; checked on the register route (30/hour).</summary>
public sealed class RateLimitTests : IClassFixture<RateLimitTests.Limited>
{
    private readonly Limited _f;

    public RateLimitTests(Limited f) => _f = f;

    public sealed class Limited : ShareFixture
    {
        public Limited() => Settings["Share:RateLimits"] = "true";
    }

    [Fact]
    public async Task TheThirtyFirstRegistrationInAnHourIsTooMany()
    {
        var http = _f.Factory.CreateClient();
        HttpResponseMessage? last = null;
        for (int i = 0; i < 31; i++)
        {
            last = await http.PostAsJsonAsync("/v1/installs", new RegisterRequest("bogus", "1.3.1.0"));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.True(last.Headers.RetryAfter is not null || last.Headers.Contains("Retry-After"));
    }
}
