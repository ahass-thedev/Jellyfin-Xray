using System.Text;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.XRay.Api;

/// <summary>
/// Intercepts GET /web/index.html and appends the X-Ray autoboot script tag,
/// so the overlay loads automatically on every Jellyfin session when enabled.
/// </summary>
public class XRayInjectMiddleware
{
    private const string ScriptTag = "<script src=\"/XRay/autoboot.js\" defer></script>";
    private readonly RequestDelegate _next;

    public XRayInjectMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var isIndexHtml = path.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase)
                       || path.Equals("/web/", StringComparison.OrdinalIgnoreCase);

        if (!isIndexHtml)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync().ConfigureAwait(false);

        if (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) == true
            && body.Contains("</body>", StringComparison.OrdinalIgnoreCase))
        {
            body = body.Replace("</body>", ScriptTag + "</body>", StringComparison.OrdinalIgnoreCase);
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength = bytes.Length;
            await originalBody.WriteAsync(bytes).ConfigureAwait(false);
        }
        else
        {
            buffer.Seek(0, SeekOrigin.Begin);
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
        }
    }
}
