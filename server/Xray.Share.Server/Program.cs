using Jellyfin.Plugin.SceneXray.Sharing;
using Microsoft.Extensions.Options;
using Xray.Share.Server;
using Xray.Share.Server.Data;

// `xray-share invite …` and `xray-share migrate` run the operator CLI (design §6: no admin
// surface on the hostname; invites are issued with a command run in the pod).
if (args.Length > 0 && Cli.Commands.Contains(args[0]))
{
    return await Cli.RunAsync(args);
}

var app = ShareApp.Build(args);
await app.RunAsync();
return 0;

public partial class Program;

namespace Xray.Share.Server
{
    public static class ShareApp
    {
        public static ShareOptions Options(IConfiguration config)
        {
            var o = new ShareOptions();
            config.GetSection("Share").Bind(o);
            return o;
        }

        public static WebApplication Build(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            Configure(builder.Services, builder.Configuration);

            // Request bodies: 256 KB as sent (design §4); the rest is checked while inflating.
            builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = Options(builder.Configuration).MaxBodyBytes);

            var app = builder.Build();
            app.UseRateLimiter();
            Endpoints.Map(app);
            return app;
        }

        public static void Configure(IServiceCollection services, IConfiguration config)
        {
            services.Configure<ShareOptions>(config.GetSection("Share"));
            var o = Options(config);
            services.AddSingleton(sp => new Db(sp.GetRequiredService<IOptions<ShareOptions>>().Value.ConnectionString()));

            // Hosted services start in order: the schema first.
            services.AddHostedService<Migrator>();
            services.AddSingleton<JobQueue>();
            services.AddHostedService<JobWorker>();
            services.AddSingleton<ConsensusService>();
            services.AddSingleton<MaintenanceService>();
            services.AddSingleton<UnknownFaceService>();
            services.AddSingleton<LifecycleService>();
            services.AddSingleton<MusicBrainzService>();
            services.AddHttpClient<IMusicBrainz, MusicBrainzClient>();
            services.AddSingleton<SubmissionService>();
            if (string.IsNullOrWhiteSpace(o.TmdbApiKey) && o.AllowWithoutTmdb)
            {
                services.AddSingleton<ICreditSource, AnyCredits>();
            }
            else
            {
                services.AddHttpClient<ICreditSource, TmdbCredits>();
            }

            RateLimits.Add(services, o);

            // No request logs with addresses: the app never reads CF-Connecting-IP (design §6).
            services.AddLogging(l => l.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning));
        }
    }
}
