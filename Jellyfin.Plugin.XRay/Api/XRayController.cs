using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.XRay.Api;

[ApiController]
[Route("[controller]")]
public class XRayController : ControllerBase
{
    // No constructor injection — services accessed via Plugin.Instance
    // since IPluginServiceRegistrator is unreliable in Jellyfin 10.11

    [HttpGet("resources")]
    [AllowAnonymous]
    public ActionResult<IEnumerable<string>> ListResources()
        => Ok(Plugin.Instance?.GetEmbeddedResourceNames() ?? Array.Empty<string>());

    [HttpGet("query")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(XRayQueryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<XRayQueryResponse> Query([FromQuery] Guid itemId, [FromQuery] int t)
    {
        if (itemId == Guid.Empty)
            return BadRequest("itemId is required");

        var store = Plugin.Instance?.Store;
        if (store is null)
            return StatusCode(503, "Plugin not ready");

        if (!store.Exists(itemId))
            return NotFound(new { message = "X-Ray data not yet generated for this item." });

        return Ok(new XRayQueryResponse(itemId, t, store.GetActorsAt(itemId, t)));
    }

    [HttpGet("overlay.js")]
    [AllowAnonymous]
    public ContentResult OverlayScript()
        => Content(Plugin.Instance?.GetOverlayScript() ?? string.Empty, "application/javascript");

    [HttpPost("analyze/{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult TriggerAnalysis([FromRoute] Guid itemId, [FromQuery] bool force = false)
    {
        if (itemId == Guid.Empty)
            return BadRequest("itemId is required");

        var xray = Plugin.Instance?.XRay;
        if (xray is null)
            return StatusCode(503, "Plugin not ready");

        _ = Task.Run(async () =>
        {
            try { await xray.AnalyzeAsync(itemId, force, CancellationToken.None).ConfigureAwait(false); }
            catch { }
        });

        return Accepted(new { itemId, status = "started" });
    }

    [HttpGet("status/{itemId}")]
    [AllowAnonymous]
    public ActionResult<XRayStatusResponse> Status([FromRoute] Guid itemId)
        => Ok(new XRayStatusResponse(itemId, Plugin.Instance?.Store?.Exists(itemId) ?? false));
}

public record XRayQueryResponse(Guid ItemId, int T, IReadOnlyList<string> Actors);
public record XRayStatusResponse(Guid ItemId, bool Ready);
