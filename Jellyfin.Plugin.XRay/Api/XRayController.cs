using Jellyfin.Plugin.XRay.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.XRay.Api;

[ApiController]
[Route("[controller]")]
public class XRayController : ControllerBase
{
    private readonly XRayStore _store;
    private readonly XRayService _xrayService;

    public XRayController(XRayStore store, XRayService xrayService)
    {
        _store = store;
        _xrayService = xrayService;
    }

    [HttpGet("query")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(XRayQueryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<XRayQueryResponse> Query([FromQuery] Guid itemId, [FromQuery] int t)
    {
        if (itemId == Guid.Empty)
            return BadRequest("itemId is required");

        if (!_store.Exists(itemId))
            return NotFound(new { message = "X-Ray data not yet generated for this item." });

        var actors = _store.GetActorsAt(itemId, t);
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

        _ = Task.Run(async () =>
        {
            try { await _xrayService.AnalyzeAsync(itemId, force, CancellationToken.None).ConfigureAwait(false); }
            catch { }
        });

        return Accepted(new { itemId, status = "started" });
    }

    [HttpGet("status/{itemId}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(XRayStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<XRayStatusResponse> Status([FromRoute] Guid itemId)
    {
        return Ok(new XRayStatusResponse(itemId, _store.Exists(itemId)));
    }
}

public record XRayQueryResponse(Guid ItemId, int T, IReadOnlyList<string> Actors);
public record XRayStatusResponse(Guid ItemId, bool Ready);
