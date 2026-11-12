using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SceneXray.Sharing;

// Wire shapes of the sharing service's v1 API (design §6). Both the plugin and the server use
// these records, so the two cannot disagree about a field name.

/// <summary>POST /v1/installs.</summary>
public sealed record RegisterRequest(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("plugin")] string Plugin);

public sealed record RegisterResponse(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("balance")] int Balance);

/// <summary>GET /v1/installs/me: the status line.</summary>
public sealed record InstallStatus(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("balance")] int Balance,
    [property: JsonPropertyName("held")] int Held,
    [property: JsonPropertyName("trust")] double Trust,
    [property: JsonPropertyName("contributed")] int Contributed,
    [property: JsonPropertyName("received")] int Received);

/// <summary>One variant in an availability answer. <c>TitleKeyHash</c> is the hex SHA-256 of the title key.</summary>
public sealed record VariantSummary(
    [property: JsonPropertyName("variantId")] Guid VariantId,
    [property: JsonPropertyName("titleKeyHash")] string TitleKeyHash,
    [property: JsonPropertyName("edition")] string Edition,
    [property: JsonPropertyName("runtime")] double Runtime,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("revision")] long Revision);

/// <summary>GET /v1/variants/{id}/reference: the public anchors and a single-use challenge nonce.</summary>
public sealed record ReferenceResponse(
    [property: JsonPropertyName("variantId")] Guid VariantId,
    [property: JsonPropertyName("runtime")] double Runtime,
    [property: JsonPropertyName("every")] double Every,
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("stride")] int Stride,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("items")] string Items,
    [property: JsonPropertyName("nonce")] string Nonce)
{
    public AnchorSet ToSet() => AnchorSet.Decode(Items, Every, Length, Stride);
}

/// <summary>The contributor's fit to an existing variant: t_local = Slope · t_canonical + Offset, trusted on [ValidFrom, ValidTo] (canonical).</summary>
public sealed record SubmissionFit(
    [property: JsonPropertyName("slope")] double Slope,
    [property: JsonPropertyName("offset")] double Offset,
    [property: JsonPropertyName("inliers")] int Inliers,
    [property: JsonPropertyName("validFrom")] double ValidFrom,
    [property: JsonPropertyName("validTo")] double ValidTo);

/// <summary>Answer to a possession challenge (see <see cref="PossessionChallenge"/>).</summary>
public sealed record ChallengeAnswer(
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("windows")] string Windows);

/// <summary>POST /v1/submissions.</summary>
public sealed record SubmissionRequest(
    [property: JsonPropertyName("contribution")] SharedContribution Contribution,
    [property: JsonPropertyName("variantId")] Guid? VariantId,
    [property: JsonPropertyName("fit")] SubmissionFit? Fit,
    [property: JsonPropertyName("challenge")] ChallengeAnswer? Challenge);

public sealed record SubmissionResult(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("submissionId")] Guid? SubmissionId,
    [property: JsonPropertyName("variantId")] Guid? VariantId,
    [property: JsonPropertyName("newVariant")] bool NewVariant,
    [property: JsonPropertyName("credit")] int Credit,
    [property: JsonPropertyName("held")] int Held,
    [property: JsonPropertyName("reason")] string? Reason)
{
    public const string Accepted = "accepted";
    public const string Derived = "derived";
    public const string Rejected = "rejected";
}

/// <summary>A consensus person: TMDb id only; the receiver takes names from its own library.</summary>
public sealed record ConsensusPerson(
    [property: JsonPropertyName("tmdbPerson")] int TmdbPerson,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("voices")] int Voices,
    [property: JsonPropertyName("intervals")] IReadOnlyList<double[]> Intervals);

public sealed record ConsensusMusicCue(
    [property: JsonPropertyName("start")] double Start,
    [property: JsonPropertyName("end")] double End,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("artist")] string? Artist,
    [property: JsonPropertyName("album")] string? Album,
    [property: JsonPropertyName("mbid")] string? Mbid,
    [property: JsonPropertyName("confidence")] double Confidence);

/// <summary>POST /v1/variants/{id}/fetch: a variant's consensus, in its canonical time.</summary>
public sealed record ConsensusTimeline(
    [property: JsonPropertyName("variantId")] Guid VariantId,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("tier")] string Tier,
    [property: JsonPropertyName("voices")] int Voices,
    [property: JsonPropertyName("runtime")] double Runtime,
    [property: JsonPropertyName("scenes")] IReadOnlyList<double[]> Scenes,
    [property: JsonPropertyName("people")] IReadOnlyList<ConsensusPerson> People,
    [property: JsonPropertyName("voicePeople")] IReadOnlyList<ConsensusPerson> VoicePeople,
    [property: JsonPropertyName("music")] IReadOnlyList<ConsensusMusicCue> Music)
{
    public const string Confirmed = "confirmed";
    public const string Unconfirmed = "unconfirmed";
}

/// <summary>GET /v1/health.</summary>
public sealed record HealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("db")] bool Db,
    [property: JsonPropertyName("minAnalyzerVersion")] int MinAnalyzerVersion,
    [property: JsonPropertyName("minPluginVersion")] string MinPluginVersion);

/// <summary>Every error body: a short machine reason and a sentence for the log.</summary>
public sealed record ShareError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("message")] string Message);

/// <summary>GET /v1/installs/me/unknowns: a name for one of this install's own unknown faces.</summary>
public sealed record UnknownName(
    [property: JsonPropertyName("variantId")] Guid VariantId,
    [property: JsonPropertyName("submissionId")] Guid SubmissionId,
    [property: JsonPropertyName("unknownId")] int UnknownId,
    [property: JsonPropertyName("tmdbPerson")] int TmdbPerson,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("voices")] int Voices,
    [property: JsonPropertyName("revision")] long Revision);
