using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SceneXray.Storage;

/// <summary>
/// Everything X-Ray knows about one title. This is also the public API shape
/// (GET /SceneXray/Items/{id}/Timeline), so changes must stay backwards compatible.
/// </summary>
public sealed class XrayTimeline
{
    public int SchemaVersion { get; set; } = 1;

    public Guid ItemId { get; set; }

    public DateTime AnalyzedAt { get; set; }

    public double DurationSeconds { get; set; }

    /// <summary>Gets or sets the media-file + analyser fingerprint; a mismatch means re-analyse.</summary>
    public string Signature { get; set; } = string.Empty;

    public AnalyzerInfo Analyzer { get; set; } = new();

    /// <summary>Gets or sets identified people, most screen time first.</summary>
    public List<TimelinePerson> People { get; set; } = [];

    public List<MusicCue> Music { get; set; } = [];

    /// <summary>Gets or sets recurring people the analyser could not name (for an admin to assign).</summary>
    public List<UnknownFace> Unknown { get; set; } = [];

    /// <summary>Gets or sets a value indicating whether music identification ran for this file.</summary>
    public bool MusicAnalyzed { get; set; }

    public AnalysisStats Stats { get; set; } = new();

    /// <summary>Gets or sets people found by their voice (cartoons, costumes, off-screen speakers).</summary>
    public List<TimelinePerson> VoicePeople { get; set; } = [];

    /// <summary>Gets or sets the fingerprint of the voice pass; a mismatch means re-run voices only.</summary>
    public string? VoiceSignature { get; set; }

    public VoiceStats? Voice { get; set; }

    /// <summary>Gets or sets scene boundaries as [start, end] pairs; people are "in scene" for whole scenes.</summary>
    public List<double[]> Scenes { get; set; } = [];

    public string? SceneSignature { get; set; }

    /// <summary>Gets or sets people an admin removed from this title; kept out on re-analysis.</summary>
    public List<Guid> RejectedPersonIds { get; set; } = [];

    /// <summary>Gets or sets unknown faces an admin named; re-applied on re-analysis.</summary>
    public List<ManualAssignment> ManualAssignments { get; set; } = [];

    /// <summary>Gets or sets where a timeline received from the sharing service came from; null for local analysis.</summary>
    public SharedInfo? Shared { get; set; }
}

/// <summary>A received timeline's origin: the variant, its consensus revision and the fit used to map it.</summary>
public sealed class SharedInfo
{
    public Guid VariantId { get; set; }

    public long Revision { get; set; }

    public int Version { get; set; }

    /// <summary>Gets or sets "confirmed" or "unconfirmed".</summary>
    public string Tier { get; set; } = string.Empty;

    public double Slope { get; set; }

    public double Offset { get; set; }

    public double ValidFrom { get; set; }

    public double ValidTo { get; set; }

    /// <summary>
    /// Gets or sets the piecewise fit of another cut, as [canonical from, canonical to, offset]
    /// (all with <see cref="Slope"/>); null for a plain fit. Such a timeline is partial: the title
    /// is still analysed locally later.
    /// </summary>
    public List<double[]>? Segments { get; set; }

    public DateTime ReceivedAt { get; set; }
}

public sealed class ManualAssignment
{
    public Guid PersonId { get; set; }

    public string? UnknownEmbedding { get; set; }

    /// <summary>Gets or sets where the name came from: null for an admin, "sharing" for the sharing service.</summary>
    public string? Source { get; set; }

    /// <summary>Gets or sets a value indicating whether this name has taught the face gallery (a shared name only does at 2 voices).</summary>
    public bool Taught { get; set; } = true;
}

public sealed class AnalyzerInfo
{
    public string Version { get; set; } = string.Empty;

    public string ModelPack { get; set; } = string.Empty;

    public double SampleFps { get; set; }
}

public sealed class TimelinePerson
{
    public Guid PersonId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Role { get; set; }

    public double ScreenTimeSeconds { get; set; }

    public double Confidence { get; set; }

    /// <summary>Gets or sets [start, end] pairs in seconds, sorted and non-overlapping.</summary>
    public List<double[]> Intervals { get; set; } = [];

    /// <summary>Gets or sets how this person was found: "face", "voice" (plus where the name came from).</summary>
    public string Source { get; set; } = "face";

    /// <summary>Gets or sets raw on-screen spans (faces) before scene bridging. Clients get them merged, to rank who is on screen first.</summary>
    public List<double[]>? Spans { get; set; }
}

public sealed class UnknownFace
{
    public int Id { get; set; }

    public double ScreenTimeSeconds { get; set; }

    public List<double[]> Intervals { get; set; } = [];

    /// <summary>Gets or sets the time of the clearest sighting, for the thumbnail.</summary>
    public double ThumbTime { get; set; }

    /// <summary>Gets or sets that sighting's face box as fractions of the frame: x1, y1, x2, y2.</summary>
    public float[] ThumbBox { get; set; } = [];

    /// <summary>Gets or sets the cluster's mean embedding, base64 (stored, never sent to clients).</summary>
    public string? Embedding { get; set; }

    /// <summary>Gets or sets the best guess from the cast, if any came close.</summary>
    public Guid? SuggestedPersonId { get; set; }
}

public sealed class MusicCue
{
    public double Start { get; set; }

    public double End { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Artist { get; set; }

    public string? Album { get; set; }

    /// <summary>Gets or sets where the match came from: "library" or "audd".</summary>
    public string Source { get; set; } = "library";

    /// <summary>Gets or sets the Jellyfin audio item, when the song is in the library.</summary>
    public Guid? ItemId { get; set; }

    public double Confidence { get; set; }
}

public sealed class VoiceStats
{
    public int Lines { get; set; }

    public int Embedded { get; set; }

    public int Voices { get; set; }

    public int Named { get; set; }

    public double Seconds { get; set; }

    /// <summary>Gets or sets why voices were skipped, if they were.</summary>
    public string? Note { get; set; }
}

public sealed class AnalysisStats
{
    public int Frames { get; set; }

    public int Faces { get; set; }

    public int Tracks { get; set; }

    public int Clusters { get; set; }

    public int Identified { get; set; }

    public double Seconds { get; set; }
}

/// <summary>One JSON file per item under the plugin's data folder.</summary>
public sealed class TimelineStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _dir;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (DateTime Written, TimelineHead Head)> _heads = new();

    public TimelineStore(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    private string PathFor(Guid id) => Path.Combine(_dir, id.ToString("N") + ".json");

    public XrayTimeline? Get(Guid itemId)
    {
        string p = PathFor(itemId);
        if (!File.Exists(p))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<XrayTimeline>(File.ReadAllText(p), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(XrayTimeline t)
    {
        string p = PathFor(t.ItemId);
        string tmp = p + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(t, Json));
        File.Move(tmp, p, overwrite: true);
    }

    public void Delete(Guid itemId)
    {
        File.Delete(PathFor(itemId));
        _heads.TryRemove(itemId, out _);
    }

    /// <summary>
    /// The few fields needed to list titles and to tell whether one is up to date, without reading
    /// the whole timeline (unknown faces carry embeddings) every time. Re-read when the file changes.
    /// </summary>
    public TimelineHead? Head(Guid itemId)
    {
        var info = new FileInfo(PathFor(itemId));
        if (!info.Exists)
        {
            _heads.TryRemove(itemId, out _);
            return null;
        }

        if (_heads.TryGetValue(itemId, out var cached) && cached.Written == info.LastWriteTimeUtc)
        {
            return cached.Head;
        }

        if (Get(itemId) is not { } t)
        {
            return null;
        }

        var head = TimelineHead.Of(t);
        _heads[itemId] = (info.LastWriteTimeUtc, head);
        return head;
    }

    public IEnumerable<Guid> All() =>
        Directory.EnumerateFiles(_dir, "*.json")
            .Select(f => Guid.TryParseExact(Path.GetFileNameWithoutExtension(f), "N", out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty);
}

/// <summary>A timeline's signatures and counts (see <see cref="TimelineStore.Head"/>).</summary>
public sealed record TimelineHead(
    string Signature,
    string? VoiceSignature,
    string? SceneSignature,
    DateTime AnalyzedAt,
    int People,
    int VoicePeople,
    int Songs,
    int Unknown,
    bool Shared,
    DateTime? ReceivedAt)
{
    public static TimelineHead Of(XrayTimeline t) => new(
        t.Signature,
        t.VoiceSignature,
        t.SceneSignature,
        t.AnalyzedAt,
        t.People.Count,
        t.VoicePeople.Count,
        t.Music.Count,
        t.Unknown.Count,
        t.Shared is not null,
        t.Shared?.ReceivedAt);
}
