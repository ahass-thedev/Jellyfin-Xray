using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.SceneXray.Sharing;

/// <summary>
/// The possession challenge (design §3, "Contributors align too"). A reference response carries a
/// single-use nonce; from it both sides derive canonical anchor positions among the anchors that
/// were <em>not</em> published. The contributor returns its own fingerprint around each position,
/// mapped through its declared fit, and the server compares those windows with the stored
/// canonical anchors by bit-error rate. Without the audio, and without the right fit, nobody can
/// answer.
/// </summary>
public static class PossessionChallenge
{
    /// <summary>Positions asked per challenge.</summary>
    public const int Positions = 12;

    /// <summary>Items of slack on each side of a window: rounding and fit error (±0.5 s).</summary>
    public const int SlackItems = 4;

    /// <summary>Share of informative positions that must match.</summary>
    public const double MinPassFraction = 0.75;

    /// <summary>Informative positions needed for a verdict at all.</summary>
    public const int MinInformative = 4;

    public static string NewNonce() => Base64Url.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>
    /// Anchor indices (of the full, stride-1 set of <paramref name="anchorCount"/>) derived from
    /// <paramref name="nonce"/>, never one the reference published (multiples of <paramref name="stride"/>).
    /// </summary>
    public static IReadOnlyList<int> Pick(string nonce, int anchorCount, int stride, int count = Positions)
    {
        var hidden = Enumerable.Range(0, anchorCount).Where(i => stride <= 1 || i % stride != 0).ToList();
        var picked = new List<int>();
        for (uint counter = 0; picked.Count < Math.Min(count, hidden.Count) && counter < 10_000; counter++)
        {
            byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(nonce + ":" + counter));
            int i = hidden[(int)(BitConverter.ToUInt32(h, 0) % (uint)hidden.Count)];
            if (!picked.Contains(i))
            {
                picked.Add(i);
            }
        }

        return picked;
    }

    /// <summary>Canonical start time (s) of anchor <paramref name="index"/> of a full set spaced <paramref name="every"/> s.</summary>
    public static double AnchorTime(int index, double every) =>
        Math.Round(index * every / FingerprintMatcher.ItemSeconds) * FingerprintMatcher.ItemSeconds;

    /// <summary>
    /// The contributor's answer: for each position, <c>length + 2·SlackItems</c> items of
    /// <paramref name="fingerprint"/> centred on where the anchor's audio is in it.
    /// <paramref name="toFingerprintTime"/> maps canonical seconds to that fingerprint's time base
    /// (the fit, divided by the PAL speed when the PAL retry produced it). Missing audio is zeros.
    /// </summary>
    public static string Answer(uint[] fingerprint, IReadOnlyList<int> indices, double every, int length, Func<double, double> toFingerprintTime)
    {
        int w = length + (2 * SlackItems);
        var all = new uint[indices.Count * w];
        for (int k = 0; k < indices.Count; k++)
        {
            int start = (int)Math.Round(toFingerprintTime(AnchorTime(indices[k], every)) / FingerprintMatcher.ItemSeconds) - SlackItems;
            for (int j = 0; j < w; j++)
            {
                int at = start + j;
                all[(k * w) + j] = at >= 0 && at < fingerprint.Length ? fingerprint[at] : 0;
            }
        }

        return Convert.ToBase64String(Chromaprint.ToBytes(all));
    }

    /// <summary>
    /// Server side: how many informative positions matched, out of how many were informative.
    /// <paramref name="canonical"/> is the variant's full anchor set (stride 1).
    /// </summary>
    public static (int Passed, int Informative) Verify(AnchorSet canonical, IReadOnlyList<int> indices, string windows, double maxBitErrorRate = FingerprintMatcher.MaxBitErrorRate)
    {
        uint[] items;
        try
        {
            items = Chromaprint.FromBytes(Convert.FromBase64String(windows));
        }
        catch (FormatException)
        {
            return (0, 0);
        }

        int len = canonical.Length, w = len + (2 * SlackItems);
        if (items.Length != indices.Count * w)
        {
            return (0, 0);
        }

        int passed = 0, informative = 0;
        for (int k = 0; k < indices.Count; k++)
        {
            if (indices[k] >= canonical.Count || !AnchorSet.IsInformative(canonical.Windows[indices[k]]))
            {
                continue;
            }

            informative++;
            var anchor = canonical.Windows[indices[k]];
            double best = 1;
            for (int shift = 0; shift + len <= w; shift++)
            {
                int err = 0;
                for (int i = 0; i < len; i++)
                {
                    err += BitOperations.PopCount(items[(k * w) + shift + i] ^ anchor[i]);
                }

                best = Math.Min(best, err / (32.0 * len));
            }

            if (best <= maxBitErrorRate)
            {
                passed++;
            }
        }

        return (passed, informative);
    }

    /// <summary>The verdict on <see cref="Verify"/>'s counts.</summary>
    public static bool Passes(int passed, int informative) =>
        informative >= MinInformative && passed >= Math.Ceiling(MinPassFraction * informative);
}
