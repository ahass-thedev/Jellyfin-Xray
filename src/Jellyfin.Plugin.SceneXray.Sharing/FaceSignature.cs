using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.SceneXray.Sharing;

/// <summary>
/// An unknown face's mean embedding on the wire (design §2): 512 × float16, little-endian, base64,
/// about 1.4 KB. Only unknown faces travel, and only one way.
/// </summary>
public static class FaceSignature
{
    public const int Dimensions = 512;

    /// <summary>Recognizers whose embeddings may be shared; each lives in its own space.</summary>
    public static readonly IReadOnlyList<string> Recognizers = ["w600k_r50", "w600k_mbf"];

    public static string? Encode(float[]? v)
    {
        if (v is null || v.Length != Dimensions || v.Any(x => !float.IsFinite(x)))
        {
            return null;
        }

        var half = v.Select(x => (Half)x).ToArray();
        return Convert.ToBase64String(MemoryMarshal.AsBytes(half.AsSpan()));
    }

    /// <summary>The embedding, or null when it is not exactly 512 finite, non-zero values.</summary>
    public static float[]? Decode(string? b64)
    {
        if (string.IsNullOrEmpty(b64))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(b64);
        }
        catch (FormatException)
        {
            return null;
        }

        if (bytes.Length != Dimensions * 2)
        {
            return null;
        }

        var v = MemoryMarshal.Cast<byte, Half>(bytes).ToArray().Select(h => (float)h).ToArray();
        return v.All(float.IsFinite) && v.Any(x => x != 0) ? v : null;
    }
}
