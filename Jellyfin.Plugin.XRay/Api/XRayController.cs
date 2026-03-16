using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.XRay.Api;

/// <summary>
/// API controller exposing X-Ray endpoints to the web player.
/// Services are resolved from Plugin.Instance (standard Jellyfin plugin pattern).
///
/// Routes:
///   GET  /XRay/query?itemId={id}&amp;t={seconds}
///   GET  /XRay/overlay.js
///   POST /XRay/analyze/{itemId}
///   GET  /XRay/status/{itemId}
/// </summary>
[ApiController]
[Route("[controller]")]
public class XRayController : ControllerBase
{
    [HttpGet("query")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(XRayQueryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<XRayQueryResponse> Query([FromQuery] Guid itemId, [FromQuery] int t)
    {
        if (itemId == Guid.Empty)
            return BadRequest("itemId is required");

        var store = Plugin.Instance?.Store;
        if (store is null)
            return StatusCode(503, "Plugin not initialised");

        if (!store.Exists(itemId))
            return NotFound(new { message = "X-Ray data not yet generated for this item." });

        var actors = store.GetActorsAt(itemId, t);
        return Ok(new XRayQueryResponse(itemId, t, actors));
    }

    [HttpGet("overlay.js")]
    [AllowAnonymous]
    public ContentResult OverlayScript()
    {
        var script = Plugin.Instance?.GetOverlayScript() ?? string.Empty;
        return Content(script, "application/javascript");
    }

    [HttpPost("analyze/{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult TriggerAnalysis([FromRoute] Guid itemId, [FromQuery] bool force = false)
    {
        if (itemId == Guid.Empty)
            return BadRequest("itemId is required");

        var xray = Plugin.Instance?.XRay;
        if (xray is null)
            return StatusCode(503, "Plugin not initialised");

        _ = Task.Run(async () =>
        {
            try { await xray.AnalyzeAsync(itemId, force, CancellationToken.None).ConfigureAwait(false); }
            catch { /* logged inside AnalyzeAsync */ }
        });

        return Accepted(new { itemId, status = "started" });
    }

    [HttpGet("status/{itemId}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(XRayStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<XRayStatusResponse> Status([FromRoute] Guid itemId)
    {
        var ready = Plugin.Instance?.Store.Exists(itemId) ?? false;
        return Ok(new XRayStatusResponse(itemId, ready));
    }
}

public record XRayQueryResponse(Guid ItemId, int T, IReadOnlyList<string> Actors);
public record XRayStatusResponse(Guid ItemId, bool Ready);
