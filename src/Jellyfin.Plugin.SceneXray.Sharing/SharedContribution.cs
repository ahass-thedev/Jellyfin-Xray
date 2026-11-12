using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SceneXray.Sharing;

// The wire format of a contribution (design §2). Built field by field from the local timeline,
// never by trimming it, so a field added to XrayTimeline later cannot leak. A unit test pins the
// field list: changing it is a deliberate, reviewed act.

public sealed record SharedContribution(
    [property: JsonPropertyName("title")] SharedTitle Title,
    [property: JsonPropertyName("analyzer")] SharedAnalyzer Analyzer,
    [property: JsonPropertyName("anchors")] SharedAnchors Anchors,
    [property: JsonPropertyName("scenes")] IReadOnlyList<double[]> Scenes,
    [property: JsonPropertyName("people")] IReadOnlyList<SharedPerson> People,
    [property: JsonPropertyName("voicePeople")] IReadOnlyList<SharedPerson> VoicePeople,
    [property: JsonPropertyName("unknownFaces")] IReadOnlyList<SharedUnknownFace> UnknownFaces,
    [property: JsonPropertyName("music")] IReadOnlyList<SharedMusicCue> Music);

public sealed record SharedTitle(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("tmdb")] int? Tmdb,
    [property: JsonPropertyName("imdb")] string? Imdb,
    [property: JsonPropertyName("series")] SharedSeries? Series,
    [property: JsonPropertyName("season")] int? Season,
    [property: JsonPropertyName("episode")] int? Episode,
    [property: JsonPropertyName("episodeTmdb")] int? EpisodeTmdb,
    [property: JsonPropertyName("edition")] string Edition,
    [property: JsonPropertyName("runtime")] double Runtime,
    [property: JsonPropertyName("frameRate")] double? FrameRate);

public sealed record SharedSeries(
    [property: JsonPropertyName("tmdb")] int? Tmdb,
    [property: JsonPropertyName("tvdb")] int? Tvdb);

public sealed record SharedAnalyzer(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("plugin")] string Plugin,
    [property: JsonPropertyName("modelPack")] string ModelPack,
    [property: JsonPropertyName("sampleFps")] double SampleFps,
    [property: JsonPropertyName("voiceModel")] string? VoiceModel);

/// <summary>Audio anchors: base64 of <c>Length</c>-item Chromaprint windows every <c>Every</c> seconds (see <see cref="AnchorSet"/>).</summary>
public sealed record SharedAnchors(
    [property: JsonPropertyName("every")] double Every,
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("items")] string Items)
{
    public static SharedAnchors From(AnchorSet set) => new(set.EverySeconds, set.Length, set.Encode());

    public AnchorSet ToSet() => AnchorSet.Decode(Items, Every, Length);
}

/// <summary>A named person, by TMDb id only: names and Jellyfin GUIDs are never sent.</summary>
public sealed record SharedPerson(
    [property: JsonPropertyName("tmdbPerson")] int TmdbPerson,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("intervals")] IReadOnlyList<double[]> Intervals);

/// <summary>A recurring face the analyser could not name: mean embedding (base64, 512 × float16), never an image.</summary>
public sealed record SharedUnknownFace(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("recognizer")] string Recognizer,
    [property: JsonPropertyName("embedding")] string Embedding,
    [property: JsonPropertyName("screenTime")] double ScreenTime,
    [property: JsonPropertyName("intervals")] IReadOnlyList<double[]> Intervals);

public sealed record SharedMusicCue(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string? Artist,
    [property: JsonPropertyName("album")] string? Album,
    [property: JsonPropertyName("mbid")] string? Mbid,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("confidence")] double Confidence);
