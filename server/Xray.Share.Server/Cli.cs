using Dapper;
using Jellyfin.Plugin.SceneXray.Sharing;
using Xray.Share.Server.Data;

namespace Xray.Share.Server;

/// <summary>
/// Operator commands, run in the pod (<c>kubectl exec … -- ./xray-share invite create …</c>):
/// <list type="bullet">
/// <item><c>migrate</c>: apply the schema.</item>
/// <item><c>invite create --note NAME [--access-client-id ID] [--max-installs N]</c>: prints the invite
/// line once. The Access client secret is read from <c>XRAY_ACCESS_CLIENT_SECRET</c> or stdin, so it
/// never appears in a process list or shell history; it is put in the line and not stored.</item>
/// <item><c>invite list</c>, <c>invite revoke ID</c>.</item>
/// </list>
/// </summary>
public static class Cli
{
    public static readonly IReadOnlySet<string> Commands = new HashSet<string> { "invite", "migrate" };

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null, TextReader? input = null)
    {
        output ??= Console.Out;
        var config = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true).AddEnvironmentVariables().Build();
        var o = ShareApp.Options(config);
        await using var db = new Db(o.ConnectionString());
        await db.MigrateAsync().ConfigureAwait(false);
        return await RunAsync(args, db, o, output, input ?? Console.In).ConfigureAwait(false);
    }

    public static async Task<int> RunAsync(string[] args, Db db, ShareOptions o, TextWriter output, TextReader input)
    {
        string Opt(string name, string fallback = "")
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }

        await using var conn = await db.OpenAsync().ConfigureAwait(false);
        switch (args.ElementAtOrDefault(0), args.ElementAtOrDefault(1))
        {
            case ("migrate", _):
                await output.WriteLineAsync("schema up to date").ConfigureAwait(false);
                return 0;

            case ("invite", "create"):
            {
                string note = Opt("--note");
                if (string.IsNullOrWhiteSpace(note))
                {
                    await output.WriteLineAsync("--note NAME is required (who the invite is for)").ConfigureAwait(false);
                    return 2;
                }

                string clientId = Opt("--access-client-id");
                string secret = string.Empty;
                if (clientId.Length > 0)
                {
                    secret = Environment.GetEnvironmentVariable("XRAY_ACCESS_CLIENT_SECRET") ?? (await input.ReadLineAsync().ConfigureAwait(false) ?? string.Empty).Trim();
                    if (secret.Length == 0)
                    {
                        await output.WriteLineAsync("the Access client secret is missing (XRAY_ACCESS_CLIENT_SECRET or stdin)").ConfigureAwait(false);
                        return 2;
                    }
                }

                int max = int.TryParse(Opt("--max-installs", o.MaxInstallsPerInvite.ToString(System.Globalization.CultureInfo.InvariantCulture)), out int m) ? Math.Clamp(m, 1, 10) : o.MaxInstallsPerInvite;
                string code = Invite.NewCode();
                Guid id = await conn.ExecuteScalarAsync<Guid>(
                    "insert into invites (code_hash, access_client_id, note, max_installs) values (@hash, @clientId, @note, @max) returning id",
                    new { hash = Secrets.Hash(code), clientId = clientId.Length > 0 ? clientId : null, note, max }).ConfigureAwait(false);
                await output.WriteLineAsync($"invite {id} for {note}, up to {max} installs:").ConfigureAwait(false);
                await output.WriteLineAsync(new Invite(code, clientId, secret).Format()).ConfigureAwait(false);
                return 0;
            }

            case ("invite", "list"):
                foreach (var r in await conn.QueryAsync<(Guid Id, string? Note, int Used, int Max, DateTime Created, DateTime? Revoked)>(
                    "select id, note, installs_used, max_installs, created_at, revoked_at from invites order by created_at").ConfigureAwait(false))
                {
                    await output.WriteLineAsync($"{r.Id}  {r.Note}  {r.Used}/{r.Max} installs  {r.Created:yyyy-MM-dd}{(r.Revoked is null ? string.Empty : "  revoked")}").ConfigureAwait(false);
                }

                return 0;

            case ("invite", "revoke") when Guid.TryParse(args.ElementAtOrDefault(2), out var revoke):
                int n = await conn.ExecuteAsync("update invites set revoked_at = now() where id = @revoke and revoked_at is null", new { revoke }).ConfigureAwait(false);
                await output.WriteLineAsync(n == 1 ? "revoked; also revoke its Cloudflare Access service token" : "no such active invite").ConfigureAwait(false);
                return n == 1 ? 0 : 1;

            default:
                await output.WriteLineAsync("usage: xray-share migrate | invite create --note NAME [--access-client-id ID] [--max-installs N] | invite list | invite revoke ID").ConfigureAwait(false);
                return 2;
        }
    }
}
