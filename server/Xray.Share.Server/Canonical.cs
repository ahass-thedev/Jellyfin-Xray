using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SceneXray.Sharing;

namespace Xray.Share.Server;

/// <summary>A submission's timeline mapped into its variant's canonical time (design §4, step 1).</summary>
public sealed record CanonicalTimeline(
    [property: JsonPropertyName("scenes")] IReadOnlyList<double[]> Scenes,
    [property: JsonPropertyName("people")] IReadOnlyList<SharedPerson> People,
    [property: JsonPropertyName("voicePeople")] IReadOnlyList<SharedPerson> VoicePeople,
    [property: JsonPropertyName("music")] IReadOnlyList<SharedMusicCue> Music)
{
    /// <summary>Maps a contribution through its fit (identity for the contribution that created the variant).</summary>
    public static CanonicalTimeline From(SharedContribution c, SubmissionFit fit, double canonicalRuntime)
    {
        List<double[]> Map(IEnumerable<double[]> intervals) =>
            TimelineMapping.ToCanonical(intervals, fit.Slope, fit.Offset, fit.ValidFrom, fit.ValidTo, canonicalRuntime);

        IReadOnlyList<SharedPerson> People(IEnumerable<SharedPerson> people) => people
            .Select(p => p with { Intervals = Map(p.Intervals) })
            .Where(p => p.Intervals.Count > 0)
            .ToList();

        var music = c.Music
            .Select(m => (Cue: m, Span: Map([[m.Start, m.End]])))
            .Where(x => x.Span.Count == 1)
            .Select(x => x.Cue with { Start = x.Span[0][0], End = x.Span[0][1] })
            .ToList();
        return new CanonicalTimeline(Map(c.Scenes), People(c.People), People(c.VoicePeople), music);
    }

    public static SubmissionFit Identity(double runtime) => new(1, 0, 0, 0, runtime);
}

/// <summary>Gzipped JSON, the storage form of bodies (design §6).</summary>
public static class Gz
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static byte[] Pack<T>(T value)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            JsonSerializer.Serialize(gz, value, Json);
        }

        return ms.ToArray();
    }

    public static T Unpack<T>(byte[] data)
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<T>(gz, Json) ?? throw new InvalidDataException("Empty body.");
    }

    public static byte[] Hash<T>(T value) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json));
}
