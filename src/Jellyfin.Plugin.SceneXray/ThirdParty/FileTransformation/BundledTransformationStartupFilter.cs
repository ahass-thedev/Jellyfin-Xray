// Vendored from File Transformation (https://github.com/IAmParadox27/jellyfin-plugin-file-transformation),
// commit 2dd4279bc7ae, Copyright (c) IAmParadox27, licensed under the GNU General Public License v3.0.
// Changes for X-Ray: renamed namespace; the read service is replaced by a single transform
// (index.html -> X-Ray's overlay script) and the virtual-file synthesis is not used.
// X-Ray only installs this copy when the File Transformation plugin itself is not installed.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SceneXray.ThirdParty.FileTransformation
{
    public sealed class BundledTransformationStartupFilter : IStartupFilter
    {
        private readonly ILogger<BundledTransformationStartupFilter> m_logger;

        public BundledTransformationStartupFilter(ILogger<BundledTransformationStartupFilter> logger)
        {
            m_logger = logger;
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                m_logger.LogInformation("X-Ray: File Transformation is not installed; using the bundled copy to add the player overlay");

                app.UseMiddleware<BundledTransformationMiddleware>();

                next(app);
            };
        }
    }
}
