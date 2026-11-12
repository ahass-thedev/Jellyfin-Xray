using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Xray.Share.Server;

/// <summary>
/// Per-install-token limits at the application (design §4, "Rate limits"). Partitions are a hash of
/// the token, never an IP. The plugin backs off on <c>429 Retry-After</c>.
/// </summary>
public static class RateLimits
{
    public const string Register = "register";
    public const string Availability = "availability";
    public const string Reference = "reference";
    public const string Submit = "submit";
    public const string Fetch = "fetch";

    public static void Add(IServiceCollection services, ShareOptions o)
    {
        services.AddRateLimiter(r =>
        {
            r.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            r.OnRejected = (ctx, _) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                {
                    ctx.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };

            Policy(r, Register, 30, TimeSpan.FromHours(1), o);
            Policy(r, Availability, 600, TimeSpan.FromHours(1), o);
            Policy(r, Reference, 300, TimeSpan.FromHours(1), o);
            Policy(r, Submit, 120, TimeSpan.FromHours(1), o);
            Policy(r, Fetch, 300, TimeSpan.FromHours(1), o);

            // Submissions also have a daily cap.
            r.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                o.RateLimits && http.Request.Method == "POST" && http.Request.Path.StartsWithSegments("/v1/submissions")
                    ? RateLimitPartition.GetFixedWindowLimiter("day:" + Auth.Partition(http), _ => Window(2000, TimeSpan.FromDays(1)))
                    : RateLimitPartition.GetNoLimiter("none"));
        });
    }

    private static void Policy(RateLimiterOptions r, string name, int permits, TimeSpan window, ShareOptions o) =>
        r.AddPolicy(name, http => o.RateLimits
            ? RateLimitPartition.GetFixedWindowLimiter(name + ":" + Auth.Partition(http), _ => Window(permits, window))
            : RateLimitPartition.GetNoLimiter("none"));

    private static FixedWindowRateLimiterOptions Window(int permits, TimeSpan window) =>
        new() { PermitLimit = permits, Window = window, QueueLimit = 0, AutoReplenishment = true };
}
