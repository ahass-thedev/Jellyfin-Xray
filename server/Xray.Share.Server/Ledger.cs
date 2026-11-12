using System.Data;
using Dapper;

namespace Xray.Share.Server;

/// <summary>An install's credit, in minutes (design §1).</summary>
public sealed record Balance(int Spendable, int Held);

/// <summary>
/// The credit ledger: append-only rows, the balance computed from them. Earned credit is split
/// into a spendable half (kind <c>earn</c>) and a held half (<c>hold</c>, held = true) that is
/// released, or cancelled, once an independent voice has looked at the same variant.
/// </summary>
public static class Ledger
{
    public const string Grace = "grace";
    public const string Earn = "earn";
    public const string Hold = "hold";
    public const string Fetch = "fetch";
    public const string Release = "release";
    public const string Cancel = "cancel";
    public const string Clawback = "clawback";

    /// <summary>Credit for <paramref name="runtimeSeconds"/> of runtime: whole minutes, at least one.</summary>
    public static int Minutes(double runtimeSeconds) => Math.Max(1, (int)Math.Round(runtimeSeconds / 60));

    /// <summary>
    /// Spendable and held credit. Credit is netted per submission (earned, released, clawed
    /// back); a submission counts as corroborated once its held half was released. Spendable
    /// uncorroborated credit counts only up to twice the corroborated credit (or the configured
    /// floor), so an install with no proven-good work lives on the grace allowance (design §1).
    /// </summary>
    public static async Task<Balance> GetAsync(IDbConnection conn, Guid installId, ShareOptions o, IDbTransaction? tx = null)
    {
        var r = await conn.QuerySingleAsync<(long Direct, long Corroborated, long Uncorroborated, long Held)>(
            """
            with per as (
              select submission_id,
                     coalesce(sum(minutes) filter (where not held), 0) as spend,
                     coalesce(sum(minutes) filter (where held), 0) as held
              from ledger where install_id = @installId group by submission_id
            )
            select
              coalesce(sum(p.spend) filter (where p.submission_id is null), 0),
              coalesce(sum(p.spend) filter (where p.submission_id is not null and s.credit_state = 'released'), 0),
              coalesce(sum(greatest(p.spend, 0)) filter (where p.submission_id is not null and coalesce(s.credit_state, 'pending') <> 'released'), 0),
              coalesce(sum(p.held), 0)
            from per p left join submissions s on s.id = p.submission_id
            """,
            new { installId },
            tx).ConfigureAwait(false);
        long uncorroborated = Math.Min(r.Uncorroborated, Math.Max(o.UncorroboratedFloorMinutes, 2 * Math.Max(0, r.Corroborated)));
        return new Balance((int)(r.Direct + r.Corroborated + uncorroborated), (int)r.Held);
    }

    public static Task AddAsync(IDbConnection conn, IDbTransaction? tx, Guid installId, string kind, int minutes, bool held = false, Guid? submissionId = null, Guid? variantId = null) =>
        conn.ExecuteAsync(
            "insert into ledger (install_id, kind, minutes, held, submission_id, variant_id) values (@installId, @kind, @minutes, @held, @submissionId, @variantId)",
            new { installId, kind, minutes, held, submissionId, variantId },
            tx);

    /// <summary>Records earned credit, half spendable and half held. Returns (spendable, held).</summary>
    public static async Task<(int Spendable, int Held)> EarnAsync(IDbConnection conn, IDbTransaction tx, Guid installId, Guid submissionId, Guid variantId, double runtimeSeconds, ShareOptions o)
    {
        int total = o.EarnMultiplier * Minutes(runtimeSeconds);
        int spendable = total / 2, held = total - spendable;
        await AddAsync(conn, tx, installId, Earn, spendable, false, submissionId, variantId).ConfigureAwait(false);
        await AddAsync(conn, tx, installId, Hold, held, true, submissionId, variantId).ConfigureAwait(false);
        return (spendable, held);
    }
}
