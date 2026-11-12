using Dapper;
using Jellyfin.Plugin.SceneXray.Sharing;
using Xray.Share.Server.Data;

namespace Xray.Share.Server;

/// <summary>An authenticated install.</summary>
public sealed record Install(Guid Id, Guid InviteId, string State, double Trust, DateTime CreatedAt);

/// <summary>Install states.</summary>
public static class InstallState
{
    public const string Member = "member";

    /// <summary>Trust fell below the floor (design §4): zero weight, no credit, no fetches.</summary>
    public const string Quarantined = "quarantined";
}

/// <summary>Bearer install tokens: only their SHA-256 is stored (design §1).</summary>
public static class Auth
{
    public static string? BearerToken(HttpContext http)
    {
        string? h = http.Request.Headers.Authorization;
        return h is not null && h.StartsWith("Bearer ", StringComparison.Ordinal) && h.Length > 7 ? h[7..].Trim() : null;
    }

    /// <summary>The rate-limit partition of a request: a hash of its token, never the token or an IP.</summary>
    public static string Partition(HttpContext http) =>
        BearerToken(http) is { } t ? Convert.ToHexString(Secrets.Hash(t).AsSpan(0, 8)) : "anonymous";

    public static async Task<Install?> InstallAsync(HttpContext http, Db db)
    {
        if (BearerToken(http) is not { } token)
        {
            return null;
        }

        await using var conn = await db.OpenAsync(http.RequestAborted).ConfigureAwait(false);

        // Last seen is kept to the hour: enough for inactivity deletion, and few writes.
        return await conn.QueryFirstOrDefaultAsync<Install>(
            """
            update installs set last_seen_at = case when last_seen_at < now() - interval '1 hour' then now() else last_seen_at end
            where token_hash = @hash
            returning id, invite_id, state, trust, created_at
            """,
            new { hash = Secrets.Hash(token) }).ConfigureAwait(false);
    }
}
