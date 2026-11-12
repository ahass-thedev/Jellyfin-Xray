namespace Xray.Share.Server;

/// <summary>Service settings: the <c>Share</c> configuration section (env <c>Share__…</c>).</summary>
public sealed class ShareOptions
{
    /// <summary>Npgsql connection string (env <c>Share__Database</c>).</summary>
    public string Database { get; set; } = string.Empty;

    /// <summary>The database password, kept apart so a deployment's secret holds only it (env <c>Share__DatabasePassword</c>).</summary>
    public string DatabasePassword { get; set; } = string.Empty;

    /// <summary><see cref="Database"/> with <see cref="DatabasePassword"/> applied, when set.</summary>
    public string ConnectionString() =>
        string.IsNullOrEmpty(DatabasePassword) ? Database : new Npgsql.NpgsqlConnectionStringBuilder(Database) { Password = DatabasePassword }.ConnectionString;

    /// <summary>Credit given at registration (design D2).</summary>
    public int GraceMinutes { get; set; } = 600;

    /// <summary>Minutes received per minute analysed (design D1).</summary>
    public int EarnMultiplier { get; set; } = 2;

    /// <summary>
    /// Spendable uncorroborated credit is capped at 2× the install's corroborated credit (§1).
    /// This floor lets an install with nothing corroborated yet spend a little of it anyway; 0 is
    /// the design's literal rule.
    /// </summary>
    public int UncorroboratedFloorMinutes { get; set; }

    public int MaxInstallsPerInvite { get; set; } = 3;

    /// <summary>Independent voices for <c>confirmed</c> (design D3).</summary>
    public int ConfirmedVoices { get; set; } = 2;

    /// <summary>Only installs older than this vote (design §4.5).</summary>
    public int VotingAgeDays { get; set; } = 7;

    /// <summary>Older analyser versions are deleted this long after a newer one is confirmed (design §4).</summary>
    public int SupersededRetentionDays { get; set; } = 90;

    /// <summary>
    /// Vote weight per analyser model pack (design D11): all equal at first. A hook to weigh, say,
    /// Fast lower if it proves noisier: <c>Share__ModelPackWeights__Fast=0.7</c>.
    /// </summary>
    public Dictionary<string, double> ModelPackWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public double ModelPackWeight(string? pack) =>
        pack is not null && ModelPackWeights.TryGetValue(pack, out double w) && double.IsFinite(w) ? Math.Clamp(w, 0, 2) : 1;

    /// <summary>Credit older than this expires (design §1: 12 months).</summary>
    public int CreditExpiryDays { get; set; } = 365;

    /// <summary>An install silent this long is deleted (design §1: 12 months).</summary>
    public int InactiveDays { get; set; } = 365;

    /// <summary>Submissions from older analysers are refused (design §4).</summary>
    public int MinAnalyzerVersion { get; set; } = 2;

    public string MinPluginVersion { get; set; } = "1.3.1.0";

    /// <summary>TMDb v3 API key for the credits check. Required unless <see cref="AllowWithoutTmdb"/>.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    public string TmdbBaseUrl { get; set; } = "https://api.themoviedb.org/3/";

    /// <summary>Local testing only: skip the TMDb credits check when no key is set.</summary>
    public bool AllowWithoutTmdb { get; set; }

    public bool RateLimits { get; set; } = true;

    /// <summary>Request bodies, as sent (gzipped or not): design §4, 256 KB.</summary>
    public int MaxBodyBytes { get; set; } = 256 * 1024;

    /// <summary>A gzipped body may inflate to at most this.</summary>
    public int MaxInflatedBytes { get; set; } = 4 * 1024 * 1024;

    public int NonceTtlHours { get; set; } = 24;

    /// <summary>Runs the job queue in this process. Tests turn it off and drain the queue themselves.</summary>
    public bool Worker { get; set; } = true;
}
