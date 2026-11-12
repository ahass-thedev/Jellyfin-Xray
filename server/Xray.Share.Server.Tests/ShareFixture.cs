using Dapper;
using Jellyfin.Plugin.SceneXray.Sharing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xray.Share.Server.Data;

namespace Xray.Share.Server.Tests;

/// <summary>Cast lists the tests control, instead of TMDb.</summary>
public sealed class FakeCredits : ICreditSource
{
    public Dictionary<string, HashSet<int>> Cast { get; } = [];

    public bool Down { get; set; }

    public Task<CastList?> CastAsync(string titleKey, SharedTitle title, CancellationToken ct)
    {
        if (Down)
        {
            throw new CreditsUnavailableException("TMDb is unreachable");
        }

        return Task.FromResult(Cast.TryGetValue(titleKey, out var ids) ? new CastList(ids) : null);
    }
}

/// <summary>MusicBrainz as the tests define it.</summary>
public sealed class FakeMusicBrainz : IMusicBrainz
{
    public Dictionary<Guid, MusicBrainzRecording> Recordings { get; } = [];

    public int Calls { get; private set; }

    public Task<MusicBrainzRecording?> RecordingAsync(Guid mbid, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Recordings.GetValueOrDefault(mbid));
    }
}

/// <summary>
/// The service in-process (WebApplicationFactory) over a fresh database per test class, on the
/// PostgreSQL named by XRAY_SHARE_TEST_DB (default: server/docker-compose.yml's db).
/// </summary>
public class ShareFixture : IAsyncLifetime
{
    public const string DefaultServer = "Host=localhost;Port=55432;Username=xray;Database=xray";

    private readonly string _name = "xray_t_" + Guid.NewGuid().ToString("N")[..12];
    private string _admin = string.Empty;

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public FakeCredits Credits { get; } = new();

    public FakeMusicBrainz MusicBrainz { get; } = new();

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>Overrides applied to the service's configuration (key → value).</summary>
    public Dictionary<string, string?> Settings { get; } = new() { ["Share:RateLimits"] = "false" };

    public async Task InitializeAsync()
    {
        _admin = Environment.GetEnvironmentVariable("XRAY_SHARE_TEST_DB") ?? DefaultServer;
        await using (var conn = new NpgsqlConnection(_admin))
        {
            await conn.ExecuteAsync($"create database {_name}");
        }

        ConnectionString = new NpgsqlConnectionStringBuilder(_admin) { Database = _name }.ConnectionString;
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Share:Database", ConnectionString);
            b.UseSetting("Share:Worker", "false");
            foreach (var (k, v) in Settings)
            {
                b.UseSetting(k, v);
            }

            b.ConfigureServices(s =>
            {
                s.RemoveAll<ICreditSource>();
                s.AddSingleton<ICreditSource>(Credits);
                s.RemoveAll<IMusicBrainz>();
                s.AddSingleton<IMusicBrainz>(MusicBrainz);
            });
        });
        _ = Factory.Server; // start: runs the migrations
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_admin);
        await conn.ExecuteAsync($"drop database if exists {_name} with (force)");
    }

    public Db Db => Factory.Services.GetRequiredService<Db>();

    public Task<int> DrainAsync() => Factory.Services.GetRequiredService<JobQueue>().DrainAsync();

    public async Task<Invite> InviteAsync(string note = "friend", int? maxInstalls = null)
    {
        var o = ShareApp.Options(Factory.Services.GetRequiredService<IConfiguration>());
        var output = new StringWriter();
        string[] args = maxInstalls is { } m ? ["invite", "create", "--note", note, "--max-installs", m.ToString(System.Globalization.CultureInfo.InvariantCulture)] : ["invite", "create", "--note", note];
        Assert.Equal(0, await Cli.RunAsync(args, Db, o, output, new StringReader(string.Empty)));
        return Invite.TryParse(output.ToString().Split('\n')[1])!;
    }

    public ShareClient Client(Invite? invite = null, string? token = null) =>
        new(Factory.CreateClient(), new Uri("http://localhost/"), invite, token);

    /// <summary>A registered install's client.</summary>
    public async Task<ShareClient> MemberAsync(Invite? invite = null)
    {
        invite ??= await InviteAsync();
        var r = await Client(invite).RegisterAsync("1.3.1.0");
        return Client(invite, r.Token);
    }

    /// <summary>
    /// Makes installs older (design §4: only installs older than 7 days vote), then runs the
    /// maintenance that notices it, as the hourly job would.
    /// </summary>
    public async Task AgeInstallsAsync(TimeSpan by)
    {
        await using (var conn = await Db.OpenAsync())
        {
            await conn.ExecuteAsync("update installs set created_at = created_at - @by", new { by });
        }

        await Factory.Services.GetRequiredService<MaintenanceService>().RunAsync(default);
    }

    public async Task<T> ScalarAsync<T>(string sql, object? args = null)
    {
        await using var conn = await Db.OpenAsync();
        return await conn.ExecuteScalarAsync<T>(sql, args);
    }

    public async Task ExecAsync(string sql, object? args = null)
    {
        await using var conn = await Db.OpenAsync();
        await conn.ExecuteAsync(sql, args);
    }
}
