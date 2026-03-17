using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.XRay.Api;

/// <summary>
/// Inserts <see cref="XRayInjectMiddleware"/> at the front of the ASP.NET Core
/// pipeline so it runs before Jellyfin's static-file middleware.
/// </summary>
public class XRayStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        => app =>
        {
            app.UseMiddleware<XRayInjectMiddleware>();
            next(app);
        };
}
