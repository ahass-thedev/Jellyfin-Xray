using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Jellyfin.Plugin.SceneXray.Sharing;
using Microsoft.Extensions.Options;
using Xray.Share.Server.Data;

namespace Xray.Share.Server;

/// <summary>The v1 routes (design §6). Only <c>/v1/*</c> exists; everything else is 404.</summary>
public static class Endpoints
{
    /// <summary>Request bodies: unknown fields are an error, so nothing unexpected is parsed.</summary>
    public static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    public static IResult Error(int status, string error, string message) => Results.Json(new ShareError(error, message), statusCode: status);

    private static IResult Unauthorized() => Error(401, "unauthorized", "a valid install token is required");

    public static void Map(WebApplication app)
    {
        var v1 = app.MapGroup("/v1");

        v1.MapGet("/health", async (Db db, IOptions<ShareOptions> o, CancellationToken ct) =>
        {
            bool ok;
            try
            {
                await using var conn = await db.OpenAsync(ct);
                ok = await conn.ExecuteScalarAsync<int>("select 1") == 1;
            }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException or TimeoutException)
            {
                ok = false;
            }

            return Results.Json(new HealthResponse(ok ? "ok" : "degraded", ok, o.Value.MinAnalyzerVersion, o.Value.MinPluginVersion), statusCode: ok ? 200 : 503);
        });

        v1.MapPost("/installs", async (HttpContext http, Db db, IOptions<ShareOptions> o) =>
        {
            var req = await ReadAsync<RegisterRequest>(http, o.Value);
            if (req is null || string.IsNullOrWhiteSpace(req.Code))
            {
                return Error(400, "invalid", "registration code missing");
            }

            if (!Version.TryParse(req.Plugin, out var plugin) || plugin < Version.Parse(o.Value.MinPluginVersion))
            {
                return Error(426, "plugin-too-old", $"X-Ray {o.Value.MinPluginVersion} or newer is needed");
            }

            await using var conn = await db.OpenAsync(http.RequestAborted);
            await using var tx = await conn.BeginTransactionAsync(http.RequestAborted);
            var invite = await conn.QueryFirstOrDefaultAsync<(Guid Id, int MaxInstalls, int InstallsUsed)>(
                "select id, max_installs, installs_used from invites where code_hash = @hash and revoked_at is null for update",
                new { hash = Secrets.Hash(req.Code.Trim()) },
                tx);
            if (invite.Id == Guid.Empty)
            {
                return Error(403, "invite", "this invite is not valid");
            }

            if (invite.InstallsUsed >= invite.MaxInstalls)
            {
                return Error(403, "invite-used", $"this invite has registered its {invite.MaxInstalls} installs");
            }

            string token = Secrets.NewToken();
            Guid installId = await conn.ExecuteScalarAsync<Guid>(
                "insert into installs (token_hash, invite_id, plugin_version) values (@hash, @Id, @plugin) returning id",
                new { hash = Secrets.Hash(token), invite.Id, plugin = req.Plugin },
                tx);
            await conn.ExecuteAsync("update invites set installs_used = installs_used + 1 where id = @Id", new { invite.Id }, tx);
            await Ledger.AddAsync(conn, tx, installId, Ledger.Grace, o.Value.GraceMinutes);
            await tx.CommitAsync(http.RequestAborted);
            return Results.Json(new RegisterResponse(token, o.Value.GraceMinutes), statusCode: 201);
        }).RequireRateLimiting(RateLimits.Register);

        v1.MapGet("/installs/me", async (HttpContext http, Db db, IOptions<ShareOptions> o) =>
        {
            if (await Auth.InstallAsync(http, db) is not { } me)
            {
                return Unauthorized();
            }

            await using var conn = await db.OpenAsync(http.RequestAborted);
            var balance = await Ledger.GetAsync(conn, me.Id, o.Value);
            var counts = await conn.QuerySingleAsync<(int Contributed, int Received)>(
                """
                select count(*) filter (where kind = 'contributed')::int, count(*) filter (where kind like 'fetched%')::int
                from holdings where install_id = @Id
                """,
                new { me.Id });
            string state = me.State == InstallState.Member && balance.Spendable < 0 ? "negative" : me.State;
            return Results.Json(new InstallStatus(state, balance.Spendable, balance.Held, Math.Round(me.Trust, 3), counts.Contributed, counts.Received));
        }).RequireRateLimiting(RateLimits.Availability);

        // Names found for this install's own unknown faces (design §2). Only TMDb ids come out:
        // never an embedding, a linked group or another install's intervals. Free.
        v1.MapGet("/installs/me/unknowns", async (HttpContext http, Db db, long? since) =>
        {
            if (await Auth.InstallAsync(http, db) is not { } me)
            {
                return Unauthorized();
            }

            await using var conn = await db.OpenAsync(http.RequestAborted);
            var rows = await conn.QueryAsync<UnknownName>(
                """
                select variant_id as variantid, submission_id as submissionid, local_id as unknownid, person as tmdbperson, confidence, voices, resolved_revision as revision
                from unknown_faces
                where install_id = @Id and person is not null and resolved_revision > @since
                order by resolved_revision limit 1000
                """,
                new { me.Id, since = since ?? 0 });
            return Results.Json(rows.ToList());
        }).RequireRateLimiting(RateLimits.Availability);

        // The k-anonymous availability query (design §2, D5): the first 16 bits of SHA-256(title key)
        // name a bucket, and every variant summary in it comes back. The server never learns which
        // title was meant, or whether the install has any of them. Free.
        v1.MapGet("/availability", async (HttpContext http, Db db, IOptions<ShareOptions> o, string? prefix) =>
        {
            if (await Auth.InstallAsync(http, db) is null)
            {
                return Unauthorized();
            }

            if (prefix is null || prefix.Length != 4 || !prefix.All(Uri.IsHexDigit))
            {
                return Error(400, "invalid", "prefix must be 4 hex characters");
            }

            await using var conn = await db.OpenAsync(http.RequestAborted);
            var rows = await conn.QueryAsync<VariantRow>(
                """
                select v.id as variant_id, t.key_hash, v.edition, v.runtime, c.tier, c.version, c.revision
                from titles t join variants v on v.title_id = t.id
                join lateral (select tier, version, revision from consensus where variant_id = v.id and version >= @min
                              order by (tier = 'confirmed') desc, version desc limit 1) c on true
                where t.prefix = @prefix
                order by t.key_hash, v.created_at
                limit 2000
                """,
                new { prefix = prefix.ToLowerInvariant(), min = o.Value.MinAnalyzerVersion });
            return Results.Json(rows.Select(r => r.ToSummary()).ToList());
        }).RequireRateLimiting(RateLimits.Availability);

        // Newer consensus for variants this install fetched (design §5, refresh). Free.
        v1.MapGet("/installs/me/updates", async (HttpContext http, Db db, IOptions<ShareOptions> o, long? since) =>
        {
            if (await Auth.InstallAsync(http, db) is not { } me)
            {
                return Unauthorized();
            }

            await using var conn = await db.OpenAsync(http.RequestAborted);
            var rows = await conn.QueryAsync<VariantRow>(
                """
                select v.id as variant_id, t.key_hash, v.edition, v.runtime, c.tier, c.version, c.revision
                from holdings h join variants v on v.id = h.variant_id join titles t on t.id = v.title_id
                join lateral (select tier, version, revision from consensus where variant_id = v.id and version >= @min
                              order by (tier = 'confirmed') desc, version desc limit 1) c on true
                where h.install_id = @Id and h.kind = 'fetched' and c.revision > @since
                order by c.revision
                limit 2000
                """,
                new { me.Id, since = since ?? 0, min = o.Value.MinAnalyzerVersion });
            return Results.Json(rows.Select(r => r.ToSummary()).ToList());
        }).RequireRateLimiting(RateLimits.Availability);

        // Leave (design §5): the install, its balance, its (install, title) links and its
        // unknown-face embeddings go. Its other submissions stay as anonymous votes, unless
        // withdraw=true, which deletes them too and recomputes the consensus they were part of.
        v1.MapDelete("/installs/me", async (HttpContext http, Db db, LifecycleService lifecycle, bool? withdraw) =>
        {
            if (await Auth.InstallAsync(http, db) is not { } me)
            {
                return Unauthorized();
            }

            await lifecycle.LeaveAsync(me.Id, withdraw == true, http.RequestAborted);
            return Results.NoContent();
        }).RequireRateLimiting(RateLimits.Register);

        v1.MapGet("/variants/{id:guid}/reference", async (HttpContext http, Db db, Guid id) =>
        {
            if (await Auth.InstallAsync(http, db) is not { } me)
            {
                return Unauthorized();
            }

            await using var conn = await db.OpenAsync(http.RequestAborted);
            var v = await conn.QueryFirstOrDefaultAsync<VariantAnchors>(
                "select id, runtime, every, length, anchor_count, anchors from variants where id = @id", new { id });
            if (v is null)
            {
                return Error(404, "not-found", "no such variant");
            }

            int stride = AnchorSet.ReferenceStride(v.Runtime);
            var reference = v.ToSet().Reference(stride);
            string nonce = PossessionChallenge.NewNonce();
            await conn.ExecuteAsync(
                "insert into challenges (nonce_hash, install_id, variant_id) values (@hash, @installId, @id)",
                new { hash = Secrets.Hash(nonce), installId = me.Id, id });
            return Results.Json(new ReferenceResponse(id, v.Runtime, v.Every, v.Length, stride, v.AnchorCount, reference.Encode(), nonce));
        }).RequireRateLimiting(RateLimits.Reference);

        v1.MapPost("/submissions", async (HttpContext http, Db db, SubmissionService submissions, IOptions<ShareOptions> o) =>
        {
            if (await Auth.InstallAsync(http, db) is not { } me)
            {
                return Unauthorized();
            }

            SubmissionRequest? req;
            try
            {
                req = await ReadAsync<SubmissionRequest>(http, o.Value);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or BadHttpRequestException)
            {
                return Results.Json(new SubmissionResult(SubmissionResult.Rejected, null, null, false, 0, 0, "malformed body: " + ex.GetType().Name), statusCode: 422);
            }

            if (req?.Contribution is null)
            {
                return Results.Json(new SubmissionResult(SubmissionResult.Rejected, null, null, false, 0, 0, "missing contribution"), statusCode: 422);
            }

            try
            {
                var result = await submissions.SubmitAsync(me, req, http.RequestAborted);
                return Results.Json(result, statusCode: result.NewVariant ? 201 : 200);
            }
            catch (RejectedException ex)
            {
                return Results.Json(new SubmissionResult(SubmissionResult.Rejected, null, null, false, 0, 0, ex.Message), statusCode: 422);
            }
            catch (CreditsUnavailableException ex)
            {
                http.Response.Headers.RetryAfter = "600";
                return Error(503, "credits-unavailable", ex.Message);
            }
        }).RequireRateLimiting(RateLimits.Submit);

        v1.MapPost("/variants/{id:guid}/fetch", async (HttpContext http, Db db, IOptions<ShareOptions> o, Guid id, bool? partial) =>
        {
            if (await Auth.InstallAsync(http, db) is not { } me)
            {
                return Unauthorized();
            }

            // Quarantine is all-or-nothing, like opting out (design §4).
            if (me.State == InstallState.Quarantined)
            {
                return Error(403, "quarantined", "this install's recent submissions disagreed with others; fetching is paused");
            }

            await using var conn = await db.OpenAsync(http.RequestAborted);
            await using var tx = await conn.BeginTransactionAsync(http.RequestAborted);
            await conn.ExecuteAsync("select 1 from installs where id = @Id for update", new { me.Id }, tx);
            var c = await conn.QueryFirstOrDefaultAsync<(int Version, long Revision, byte[] Body, double Runtime)>(
                """
                select c.version, c.revision, c.body, v.runtime from consensus c join variants v on v.id = c.variant_id
                where c.variant_id = @id and c.version >= @min order by (c.tier = 'confirmed') desc, c.version desc limit 1
                """,
                new { id, min = o.Value.MinAnalyzerVersion },
                tx);
            if (c.Body is null)
            {
                return Error(404, "not-found", "no data for this variant");
            }

            bool holds = await conn.ExecuteScalarAsync<bool>(
                "select exists (select 1 from holdings where install_id = @installId and variant_id = @id)", new { installId = me.Id, id }, tx);
            if (!holds)
            {
                int cost = Ledger.Minutes(c.Runtime);
                var balance = await Ledger.GetAsync(conn, me.Id, o.Value, tx);
                if (balance.Spendable < cost)
                {
                    return Error(402, "credit", $"this title costs {cost} minutes of credit; {Math.Max(0, balance.Spendable)} available");
                }

                await Ledger.AddAsync(conn, tx, me.Id, Ledger.Fetch, -cost, variantId: id);
                // A partial fetch (another cut, mapped piecewise; phase 5) is recorded apart: analysing
                // the local cut afterwards is new work, not a copy-back of this variant.
                await conn.ExecuteAsync(
                    "insert into holdings (install_id, variant_id, kind) values (@installId, @id, @kind)",
                    new { installId = me.Id, id, kind = partial == true ? "fetched-partial" : "fetched" },
                    tx);
            }

            await tx.CommitAsync(http.RequestAborted);
            var body = Gz.Unpack<ConsensusTimeline>(c.Body) with { VariantId = id, Revision = c.Revision };
            return Results.Json(body);
        }).RequireRateLimiting(RateLimits.Fetch);
    }

    /// <summary>Reads a JSON body, inflating gzip up to the configured cap.</summary>
    public static async Task<T?> ReadAsync<T>(HttpContext http, ShareOptions o)
    {
        Stream body = http.Request.Body;
        if (string.Equals(http.Request.Headers.ContentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
        {
            body = new GZipStream(body, CompressionMode.Decompress);
        }

        await using var limited = new CappedStream(body, o.MaxInflatedBytes);
        return await JsonSerializer.DeserializeAsync<T>(limited, Strict, http.RequestAborted);
    }

    private sealed class VariantRow
    {
        public Guid VariantId { get; set; }

        public string KeyHash { get; set; } = string.Empty;

        public string Edition { get; set; } = string.Empty;

        public double Runtime { get; set; }

        public string Tier { get; set; } = string.Empty;

        public int Version { get; set; }

        public long Revision { get; set; }

        public VariantSummary ToSummary() => new(VariantId, KeyHash, Edition, Runtime, Tier, Version, Revision);
    }

    public sealed class VariantAnchors
    {
        public Guid Id { get; set; }

        public double Runtime { get; set; }

        public double Every { get; set; }

        public int Length { get; set; }

        public int AnchorCount { get; set; }

        public byte[] Anchors { get; set; } = [];

        public AnchorSet ToSet() => new(Every, Length, Chromaprint.FromBytes(Anchors).Chunk(Length).ToList());
    }
}

/// <summary>A read-only stream that fails once more than <c>max</c> bytes came through.</summary>
public sealed class CappedStream(Stream inner, long max) : Stream
{
    private long _read;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Count(int n)
    {
        _read += n;
        return _read > max ? throw new InvalidDataException("body too large") : n;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
