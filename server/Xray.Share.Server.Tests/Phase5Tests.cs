using Jellyfin.Plugin.SceneXray.Sharing;
using static Xray.Share.Server.Tests.Synth;

namespace Xray.Share.Server.Tests;

/// <summary>Phase 5: MusicBrainz-verified single-voice music, partial fetches of other cuts.</summary>
public sealed class Phase5Tests : IClassFixture<ShareFixture>
{
    private static readonly Guid Mbid = Guid.Parse("6f3c2c7e-0d0d-4a2b-9c55-0f6c1f0b5f1a");
    private readonly ShareFixture _f;

    public Phase5Tests(ShareFixture f)
    {
        _f = f;
        _f.MusicBrainz.Recordings[Mbid] = new MusicBrainzRecording("Clubbed to Death", "Rob Dougan");
    }

    private uint[] Title(int tmdb)
    {
        _f.Credits.Cast[Key(tmdb)] = [100, 101];
        return Fingerprint(3600, tmdb);
    }

    private async Task<IReadOnlyList<ConsensusMusicCue>> MusicFromOneVoiceAsync(int tmdb, SharedMusicCue cue)
    {
        var fp = Title(tmdb);
        var me = await _f.MemberAsync();
        var r = await me.SubmitAsync(new SubmissionRequest(Movie(tmdb, fp, [Person(100, [10, 500])], music: [cue]), null, null, null));
        Assert.Equal(SubmissionResult.Accepted, r.Status);
        await _f.DrainAsync();
        return (await me.FetchAsync(r.VariantId!.Value)).Music;
    }

    [Fact]
    public async Task AVerifiedRecordingIsServedFromOneVoiceWithMusicBrainzText()
    {
        var music = await MusicFromOneVoiceAsync(1101, new SharedMusicCue(300, 360, "clubbed to death", "rob d", "some album", Mbid.ToString(), "library", 0.9));
        var cue = Assert.Single(music);
        Assert.Equal("Clubbed to Death", cue.Title);
        Assert.Equal("Rob Dougan", cue.Artist);
        Assert.Null(cue.Album);
        Assert.Equal(Mbid.ToString(), cue.Mbid);
    }

    [Fact]
    public async Task AnUnverifiedOrMismatchedRecordingIsNotServedFromOneVoice()
    {
        Assert.Empty(await MusicFromOneVoiceAsync(1102, new SharedMusicCue(300, 360, "Something offensive", null, null, Mbid.ToString(), "library", 0.9)));
        Assert.Empty(await MusicFromOneVoiceAsync(1103, new SharedMusicCue(300, 360, "Clubbed to Death", null, null, Guid.NewGuid().ToString(), "library", 0.9)));
    }

    [Fact]
    public async Task AfterAPartialFetchTheLocalCutStillCounts()
    {
        var fp = Title(1104);
        var contributor = await _f.MemberAsync();
        var first = await contributor.SubmitAsync(new SubmissionRequest(Movie(1104, fp, [Person(100, [10, 500])]), null, null, null));
        await _f.DrainAsync();

        var me = await _f.MemberAsync();
        await me.FetchAsync(first.VariantId!.Value, partial: true);
        Assert.Equal(1, (await me.MeAsync()).Received);

        // The local extended cut, analysed later, starts its own variant: not a copy-back.
        var extended = Fingerprint(3600, 1104).Take(Items(1800)).Concat(Fingerprint(240, 77)).Concat(Fingerprint(3600, 1104).Skip(Items(1800))).ToArray();
        var own = await me.SubmitAsync(new SubmissionRequest(Movie(1104, extended, [Person(101, [10, 500])]), null, null, null));
        Assert.Equal(SubmissionResult.Accepted, own.Status);
        Assert.True(own.Credit + own.Held > 0);

        // Submitting to the variant it fetched partially would be.
        var match = await VariantMatcher.FindAsync(me.ReferenceAsync, (await me.AvailabilityAsync(Key(1104))).Where(v => v.VariantId == first.VariantId).ToList(), [0], (_, _, _) => Task.FromResult(fp), Runtime(fp));
        var copy = await me.SubmitAsync(new SubmissionRequest(Movie(1104, fp, [Person(100, [10, 500])]), first.VariantId, match!.ToSubmissionFit(), match.Answer()));
        Assert.Equal(SubmissionResult.Derived, copy.Status);
    }
}

/// <summary>The model-pack weighting hook (design D11).</summary>
public sealed class ModelPackWeightTests : IClassFixture<ModelPackWeightTests.FastIgnored>
{
    private readonly FastIgnored _f;

    public ModelPackWeightTests(FastIgnored f) => _f = f;

    public sealed class FastIgnored : ShareFixture
    {
        public FastIgnored() => Settings["Share:ModelPackWeights:Fast"] = "0";
    }

    [Fact]
    public async Task AZeroWeightPackDoesNotCountTowardsConfirmation()
    {
        _f.Credits.Cast[Key(1201)] = [100];
        var fp = Fingerprint(3600, 1201);
        var (a, b) = (await _f.MemberAsync(), await _f.MemberAsync());
        await a.SubmitAsync(new SubmissionRequest(Movie(1201, fp, [Person(100, [10, 500])]), null, null, null));
        await _f.DrainAsync();
        var variants = await b.AvailabilityAsync(Key(1201));
        var match = await VariantMatcher.FindAsync(b.ReferenceAsync, variants, [0], (_, _, _) => Task.FromResult(fp), Runtime(fp));
        var fast = Movie(1201, fp, [Person(100, [10, 500])]);
        await b.SubmitAsync(new SubmissionRequest(fast with { Analyzer = fast.Analyzer with { ModelPack = "Fast" } }, match!.Variant.VariantId, match.ToSubmissionFit(), match.Answer()));
        await _f.AgeInstallsAsync(TimeSpan.FromDays(8));
        await _f.DrainAsync();
        var v = (await a.AvailabilityAsync(Key(1201))).Single();
        Assert.Equal(ConsensusTimeline.Unconfirmed, v.Tier);
    }
}
