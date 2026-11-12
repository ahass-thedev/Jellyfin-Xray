using System.Reflection;
using Dapper;
using Npgsql;

namespace Xray.Share.Server.Data;

/// <summary>The one PostgreSQL database, and its embedded migrations (<c>Data/Migrations/*.sql</c>, in name order).</summary>
public sealed class Db : IAsyncDisposable
{
    static Db() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public Db(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Share:Database (env Share__Database) is not set.");
        }

        Source = NpgsqlDataSource.Create(connectionString);
    }

    public NpgsqlDataSource Source { get; }

    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default) => await Source.OpenConnectionAsync(ct).ConfigureAwait(false);

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);

        // One migrator at a time, even with several replicas starting together.
        await conn.ExecuteAsync("select pg_advisory_lock(727274)").ConfigureAwait(false);
        try
        {
            await conn.ExecuteAsync("create table if not exists schema_migrations (name text primary key, applied_at timestamptz not null default now())").ConfigureAwait(false);
            var applied = (await conn.QueryAsync<string>("select name from schema_migrations").ConfigureAwait(false)).ToHashSet();
            var asm = Assembly.GetExecutingAssembly();
            foreach (string res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                string name = res[(res.IndexOf("Migrations.", StringComparison.Ordinal) + "Migrations.".Length)..];
                if (applied.Contains(name))
                {
                    continue;
                }

                using var reader = new StreamReader(asm.GetManifestResourceStream(res)!);
                string sql = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                await conn.ExecuteAsync(sql, transaction: tx).ConfigureAwait(false);
                await conn.ExecuteAsync("insert into schema_migrations (name) values (@name)", new { name }, tx).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await conn.ExecuteAsync("select pg_advisory_unlock(727274)").ConfigureAwait(false);
        }

        // New types (pgvector) must be visible to the pool.
        await conn.ReloadTypesAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => Source.DisposeAsync();
}
