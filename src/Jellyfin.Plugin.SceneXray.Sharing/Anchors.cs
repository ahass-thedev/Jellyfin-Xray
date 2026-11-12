using System.Numerics;

namespace Jellyfin.Plugin.SceneXray.Sharing;

/// <summary>
/// Sparse audio anchors cut from a title's dense Chromaprint fingerprint (design §3): one window of
/// <see cref="Length"/> sub-fingerprints every <see cref="EverySeconds"/>. Anchor <c>k</c> starts at
/// item <c>round(k · Every / ItemSeconds)</c>, so positions are implied by the index and both sides
/// compute the same times. A window whose audio is missing (past the end) is all zeros.
/// </summary>
public sealed class AnchorSet
{
    /// <summary>Seconds between anchors in a contribution.</summary>
    public const double DefaultEverySeconds = 20;

    /// <summary>Sub-fingerprints per anchor: 24 × 0.1238 s ≈ 3 s, 96 bytes.</summary>
    public const int DefaultLength = 24;

    public AnchorSet(double everySeconds, int length, IReadOnlyList<uint[]> windows, int stride = 1)
    {
        EverySeconds = everySeconds;
        Length = length;
        Windows = windows;
        Stride = stride;
    }

    public double EverySeconds { get; }

    public int Length { get; }

    /// <summary>Anchor <c>i</c> here is anchor <c>i · Stride</c> of the full set.</summary>
    public int Stride { get; }

    public IReadOnlyList<uint[]> Windows { get; }

    public int Count => Windows.Count;

    /// <summary>First fingerprint item of anchor <paramref name="i"/> in its own (canonical) fingerprint.</summary>
    public int StartItem(int i) => (int)Math.Round(i * Stride * EverySeconds / FingerprintMatcher.ItemSeconds);

    /// <summary>Canonical time of anchor <paramref name="i"/>, in seconds.</summary>
    public double Time(int i) => StartItem(i) * FingerprintMatcher.ItemSeconds;

    /// <summary>Cuts anchors from a dense fingerprint.</summary>
    public static AnchorSet Extract(uint[] fingerprint, double everySeconds = DefaultEverySeconds, int length = DefaultLength)
    {
        var windows = new List<uint[]>();
        for (int k = 0; ; k++)
        {
            int start = (int)Math.Round(k * everySeconds / FingerprintMatcher.ItemSeconds);
            if (start + length > fingerprint.Length)
            {
                break;
            }

            windows.Add(fingerprint.AsSpan(start, length).ToArray());
        }

        return new AnchorSet(everySeconds, length, windows);
    }

    /// <summary>
    /// The public reference of a variant: every <paramref name="stride"/>-th anchor (design §3: every
    /// third, one per minute). The rest stay private for the possession challenge.
    /// </summary>
    public AnchorSet Reference(int stride) =>
        new(EverySeconds, Length, Windows.Where((_, i) => i % stride == 0).ToList(), Stride * stride);

    /// <summary>
    /// Contribution anchor spacing for a title of <paramref name="runtimeSeconds"/>. Titles under an
    /// hour get one every 10 s, so an episode's reference still holds enough anchors for the
    /// inlier rules when only its music and effects match (a dub).
    /// </summary>
    public static double EverySecondsFor(double runtimeSeconds) => runtimeSeconds >= 3600 ? DefaultEverySeconds : 10;

    /// <summary>
    /// The reference stride for a title of <paramref name="runtimeSeconds"/>: every third anchor of
    /// a film (one per minute), every second anchor of a shorter title (one per 20 s). The rest stay
    /// private for the possession challenge.
    /// </summary>
    public static int ReferenceStride(double runtimeSeconds) => runtimeSeconds >= 3600 ? 3 : 2;

    /// <summary>
    /// Whether an anchor carries enough information to be located: silence and steady hum give
    /// near-constant sub-fingerprints that match anywhere or nowhere. Measured as the mean number
    /// of bits that change between consecutive items (music and speech: 8–14 of 32).
    /// </summary>
    public static bool IsInformative(uint[] window, double minMeanBitChange = 3.0)
    {
        if (window.All(v => v == 0))
        {
            return false;
        }

        int changed = 0;
        for (int i = 1; i < window.Length; i++)
        {
            changed += BitOperations.PopCount(window[i] ^ window[i - 1]);
        }

        return changed / (double)(window.Length - 1) >= minMeanBitChange;
    }

    /// <summary>Concatenated little-endian items, base64: the DTO's <c>anchors.items</c>.</summary>
    public string Encode() => Convert.ToBase64String(Chromaprint.ToBytes(Windows.SelectMany(w => w).ToArray()));

    public static AnchorSet Decode(string items, double everySeconds, int length, int stride = 1)
    {
        var all = Chromaprint.FromBytes(Convert.FromBase64String(items));
        if (all.Length % length != 0)
        {
            throw new FormatException($"Anchor data is not a whole number of {length}-item windows.");
        }

        return new AnchorSet(everySeconds, length, all.Chunk(length).ToList(), stride);
    }
}
