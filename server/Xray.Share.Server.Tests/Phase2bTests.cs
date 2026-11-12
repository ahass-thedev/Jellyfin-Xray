using Jellyfin.Plugin.SceneXray.Sharing;
using static Xray.Share.Server.Tests.Synth;

namespace Xray.Share.Server.Tests;

/// <summary>Phase 2b: naming unknown faces together (design §2).</summary>
public sealed class Phase2bTests : IClassFixture<ShareFixture>
{
    private readonly ShareFixture _f;

    public Phase2bTests(ShareFixture f) => _f = f;

    private static float[] Face(int seed)
    {
        var rng = new Random(seed);
        var v = Enumerable.Range(0, FaceSignature.Dimensions).Select(_ => (float)(rng.NextDouble() - 0.5)).ToArray();
        return Unit(v);
    }

    private static float[] Unit(float[] v)
    {
        float n = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / n).ToArray();
    }

    // Another sighting of the same face: cosine about 0.95.
    private static float[] Again(float[] face, int seed) => Unit(face.Zip(Face(seed), (a, b) => a + (0.3f * b)).ToArray());

    private static SharedUnknownFace Unknown(int id, float[] face, params double[][] intervals) =>
        new(id, "w600k_r50", FaceSignature.Encode(face)!, TimelineMapping.Total(intervals), intervals);

    private static List<SharedPerson> Truth(int? without = null) =>
        Enumerable.Range(0, 4).Where(k => 100 + k != without).Select(k => Person(100 + k, [(k * 900) + 10, (k * 900) + 890])).ToList();

    private uint[] Title(int tmdb, params int[] cast)
    {
        _f.Credits.Cast[Key(tmdb)] = [.. cast.Length == 0 ? [100, 101, 102, 103] : cast];
        return Fingerprint(3600, tmdb);
    }

    private async Task<SubmissionResult> ContributeAsync(ShareClient c, int tmdb, uint[] fp, double lead, int seed, List<SharedPerson> people, params SharedUnknownFace[] unknowns)
    {
        await _f.DrainAsync();
        var local = lead == 0 ? fp : Release(fp, lead, seed);
        var contribution = Movie(tmdb, local, people, shift: lead) with
        {
            UnknownFaces = unknowns.Select(u => u with { Intervals = u.Intervals.Select(i => new[] { i[0] + lead, i[1] + lead }).ToList() }).ToList(),
        };
        var variants = await c.AvailabilityAsync(Key(tmdb));
        var match = variants.Count == 0 ? null : await VariantMatcher.FindAsync(c.ReferenceAsync, variants, [0], (_, _, _) => Task.FromResult(local), Runtime(local));
        return await c.SubmitAsync(new SubmissionRequest(contribution, match?.Variant.VariantId, match?.ToSubmissionFit(), match?.Answer()));
    }

    [Fact]
    public async Task AnActorHiddenFromOneInstallIsNamedByTheOthersAndSpreadsOnlyToCreditedTitles()
    {
        var actor = Face(1);

        // Install A could not name person 102 (no usable photo) in three titles. Title 902 credits
        // 102 and shows the face twice in one submission (cannot-link); 903 does not credit 102,
        // so the face match must not invent them there.
        var fp = Title(901);
        var (a, b, c) = (await _f.MemberAsync(), await _f.MemberAsync(), await _f.MemberAsync());
        var r = await ContributeAsync(a, 901, fp, 40, 2, Truth(without: 102), Unknown(7, actor, [1820, 2680]));
        Assert.Equal(SubmissionResult.Accepted, r.Status);
        var fp2 = Title(902, 100, 101, 102);
        await ContributeAsync(a, 902, fp2, 0, 0, Truth(without: 102).Take(2).ToList(), Unknown(1, Again(actor, 2), [100, 400]), Unknown(2, Again(actor, 3), [1000, 1300]));
        var fp3 = Title(903, 100, 101);
        await ContributeAsync(a, 903, fp3, 0, 0, Truth().Take(2).ToList(), Unknown(5, Again(actor, 4), [2000, 2300]));
        await _f.DrainAsync();
        Assert.Empty(await a.UnknownsAsync(0));

        // Then friends B and C analyse title 901 and recognise 102 where A's unknown is.
        await ContributeAsync(b, 901, fp, 0, 0, Truth());
        await ContributeAsync(c, 901, fp, 20, 1, Truth());

        await _f.AgeInstallsAsync(TimeSpan.FromDays(8));
        await _f.DrainAsync();

        var names = await a.UnknownsAsync(0);
        var byTime = Assert.Single(names, n => n.UnknownId == 7);
        Assert.Equal(102, byTime.TmdbPerson);
        Assert.Equal(2, byTime.Voices);
        Assert.True(byTime.Confidence >= 0.7);

        // Exactly one of title 902's two look-alikes joined the group; nothing in 903.
        Assert.Single(names, n => n.UnknownId is 1 or 2 && n.TmdbPerson == 102);
        Assert.DoesNotContain(names, n => n.UnknownId == 5);

        // Only the TMDb id is kept once named; nothing reaches the other installs.
        Assert.Equal(0, await _f.ScalarAsync<int>("select count(*) from unknown_faces where title_key in ('movie:tmdb:901', 'movie:tmdb:902', 'movie:tmdb:903') and person is not null and embedding is not null"));
        Assert.Equal(2, await _f.ScalarAsync<int>("select count(*) from unknown_faces where title_key in ('movie:tmdb:901', 'movie:tmdb:902', 'movie:tmdb:903') and person is null and embedding is not null"));
        Assert.Empty(await b.UnknownsAsync(0));

        // `since` only returns what moved.
        Assert.Empty(await a.UnknownsAsync(names.Max(n => n.Revision)));
    }

    [Fact]
    public async Task EmbeddingsNeverLiveInTheStoredBodyAndUnresolvedOnesExpire()
    {
        var fp = Title(904);
        var me = await _f.MemberAsync();
        var r = await ContributeAsync(me, 904, fp, 0, 0, Truth(), Unknown(3, Face(9), [500, 800]));
        byte[] body = await _f.ScalarAsync<byte[]>("select body from submissions where id = @Id", new { Id = r.SubmissionId });
        Assert.Empty(Gz.Unpack<SharedContribution>(body).UnknownFaces);
        Assert.Equal(1, await _f.ScalarAsync<int>("select count(*) from unknown_faces where submission_id = @Id and embedding is not null", new { Id = r.SubmissionId }));

        await _f.ExecAsync("update unknown_faces set created_at = now() - interval '13 months' where submission_id = @Id", new { Id = r.SubmissionId });
        await _f.Factory.Services.GetRequiredService<MaintenanceService>().RunAsync(default);
        Assert.Equal(0, await _f.ScalarAsync<int>("select count(*) from unknown_faces where submission_id = @Id and embedding is not null", new { Id = r.SubmissionId }));
    }

    [Fact]
    public async Task AResubmissionReplacesItsUnknownFaces()
    {
        var fp = Title(905);
        var me = await _f.MemberAsync();
        await ContributeAsync(me, 905, fp, 0, 0, Truth(), Unknown(3, Face(10), [500, 800]));
        await ContributeAsync(me, 905, fp, 0, 0, Truth(), Unknown(4, Face(11), [900, 1200]));
        const string Sql = "select {0} from unknown_faces u join submissions s on s.id = u.submission_id join variants v on v.id = s.variant_id join titles t on t.id = v.title_id where t.key = 'movie:tmdb:905'";
        Assert.Equal(1, await _f.ScalarAsync<int>(string.Format(System.Globalization.CultureInfo.InvariantCulture, Sql, "count(*)")));
        Assert.Equal(4, await _f.ScalarAsync<int>(string.Format(System.Globalization.CultureInfo.InvariantCulture, Sql, "u.local_id")));
    }

    [Theory]
    [InlineData("w600k_x", true)]
    [InlineData("short", true)]
    [InlineData("ok", false)]
    public async Task UnknownFacesAreChecked(string kind, bool rejected)
    {
        int tmdb = 906 + kind.Length;
        var fp = Title(tmdb);
        var u = Unknown(1, Face(12), [100, 200]);
        u = kind switch
        {
            "w600k_x" => u with { Recognizer = "w600k_x" },
            "short" => u with { Embedding = Convert.ToBase64String(new byte[100]) },
            _ => u,
        };
        var r = await (await _f.MemberAsync()).SubmitAsync(new SubmissionRequest(Movie(tmdb, fp, Truth()) with { UnknownFaces = [u] }, null, null, null));
        Assert.Equal(rejected, r.Status == SubmissionResult.Rejected);
    }
}

/// <summary>The naming rules on their own.</summary>
public sealed class FaceNamingTests
{
    private static ConsensusPerson P(int id, int voices, params double[][] intervals) => new(id, 0.8, voices, intervals);

    [Fact]
    public void ByTimeNeedsSeventyPercentAndAClearLead()
    {
        double[][] u = [[100, 200]];
        Assert.Equal(7, FaceNaming.ByTime(u, [P(7, 2, [90, 180])], new HashSet<int>())!.Person);
        Assert.Null(FaceNaming.ByTime(u, [P(7, 2, [100, 160])], new HashSet<int>())); // 60 %
        Assert.Null(FaceNaming.ByTime(u, [P(7, 2, [100, 200]), P(8, 2, [100, 160])], new HashSet<int>())); // runner-up too close
        Assert.Null(FaceNaming.ByTime(u, [P(7, 2, [100, 200])], new HashSet<int> { 7 })); // already named here
    }

    private static UnknownRow Row(Guid sub, string key = "movie:tmdb:1", Guid? group = null, int? person = null, string? rule = null) =>
        new(Guid.NewGuid(), sub, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, key, "w600k_r50", true, [[0, 10]], group, person, person is null ? null : 2, rule);

    [Fact]
    public void TwoUnknownsOfOneSubmissionNeverShareAGroup()
    {
        Guid s1 = Guid.NewGuid(), s2 = Guid.NewGuid();
        var a = Row(s1);
        var b = Row(s1);
        var c = Row(s2);
        var g = FaceNaming.Groups([a, b, c], [(a.Id, c.Id, 0.9), (b.Id, c.Id, 0.8), (a.Id, b.Id, 0.95)]);
        Assert.Equal(g[a.Id], g[c.Id]);
        Assert.Null(g[b.Id]);
    }

    [Fact]
    public void AGroupThatDisagreesWithItselfSpreadsNothing()
    {
        var a = Row(Guid.NewGuid());
        var b = Row(Guid.NewGuid());
        var c = Row(Guid.NewGuid());
        var groups = FaceNaming.Groups([a, b, c], [(a.Id, b.Id, 0.9), (b.Id, c.Id, 0.9)]);
        var byTime = new Dictionary<Guid, Naming> { [a.Id] = new(7, 0.9, 2, Naming.ByTime), [b.Id] = new(8, 0.9, 2, Naming.ByTime) };
        Assert.Empty(FaceNaming.Spread([a, b, c], groups, byTime, (_, _) => true, (_, _) => false));

        byTime.Remove(b.Id);
        var spread = FaceNaming.Spread([a, b, c], groups, byTime, (_, p) => p == 7, (_, _) => false);
        Assert.Equal(7, spread[b.Id].Person);
        Assert.Equal(Naming.ByFace, spread[c.Id].Rule);
        Assert.Empty(FaceNaming.Spread([a, b, c], groups, byTime, (_, p) => p != 7, (_, _) => false));
    }
}
