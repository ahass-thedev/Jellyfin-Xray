namespace Jellyfin.Plugin.SceneXray.Sharing;

/// <summary>
/// A local file aligned to one of a title's variants (design §3): the reference it was fitted to,
/// the accepted fit, and the fingerprint the fit was found in (the PAL re-fingerprint, when that
/// is what aligned), so a possession challenge can be answered from the same audio.
/// </summary>
public sealed record VariantMatch(VariantSummary Variant, ReferenceResponse Reference, AlignmentResult Fit, int AudioTrack, uint[] Fingerprint, double FingerprintSpeed)
{
    public SubmissionFit ToSubmissionFit() => new(Fit.Slope, Fit.Offset, Fit.Inliers, Fit.ValidFrom, Fit.ValidTo);

    /// <summary>Answers the reference's challenge nonce from <see cref="Fingerprint"/>.</summary>
    public ChallengeAnswer Answer()
    {
        var indices = PossessionChallenge.Pick(Reference.Nonce, Reference.Count, Reference.Stride);
        string windows = PossessionChallenge.Answer(Fingerprint, indices, Reference.Every, Reference.Length, t => Fit.ToLocal(t) / FingerprintSpeed);
        return new ChallengeAnswer(Reference.Nonce, windows);
    }
}

/// <summary>Finds the variant a local file matches, trying a few audio tracks.</summary>
public static class VariantMatcher
{
    /// <summary>
    /// Tries every audio track in <paramref name="tracks"/> (preferred first) against every
    /// variant, and returns the first accepted fit. Nothing is charged: references are free, and the
    /// metered fetch comes only after this.
    /// </summary>
    /// <param name="fingerprint">(audio track, optional <c>-af</c> chain) → raw fingerprint; should cache.</param>
    public static async Task<VariantMatch?> FindAsync(
        Func<Guid, CancellationToken, Task<ReferenceResponse>> reference,
        IReadOnlyList<VariantSummary> variants,
        IReadOnlyList<int> tracks,
        Func<int, string?, CancellationToken, Task<uint[]>> fingerprint,
        double localRuntime,
        AlignmentOptions? options = null,
        CancellationToken ct = default)
    {
        if (variants.Count == 0 || tracks.Count == 0)
        {
            return null;
        }

        var references = new Dictionary<Guid, ReferenceResponse>();
        foreach (int track in tracks)
        {
            uint[] local = await fingerprint(track, null, ct).ConfigureAwait(false);
            foreach (var v in variants)
            {
                if (!references.TryGetValue(v.VariantId, out var r))
                {
                    references[v.VariantId] = r = await reference(v.VariantId, ct).ConfigureAwait(false);
                }

                uint[]? pal = null;
                var fit = await Aligner.AlignAsync(
                    r.ToSet(),
                    local,
                    r.Runtime,
                    localRuntime,
                    async (filter, c) => pal = await fingerprint(track, filter, c).ConfigureAwait(false),
                    options,
                    ct).ConfigureAwait(false);
                if (fit.Accepted)
                {
                    bool viaPal = fit.Kind == "pal" && pal is not null;
                    double speed = viaPal ? Aligner.PalSpeed(r.Runtime, localRuntime, options) ?? 1 : 1;
                    return new VariantMatch(v, r, fit, track, viaPal ? pal! : local, speed);
                }
            }
        }

        return null;
    }
}
