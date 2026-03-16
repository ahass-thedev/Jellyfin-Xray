using Jellyfin.Plugin.XRay.Services;
using MediaBrowser.Controller.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.XRay.Api;

/// <summary>
/// API controller exposing X-Ray endpoints to the web player.
///
/// Routes:
///   GET  /XRay/query?itemId={id}&amp;t={seconds}   — actors at timestamp
///   GET  /XRay/overlay.js                        — serves the overlay script
///   POST /XRay/analyze/{itemId}                  — trigger analysis for one item
///   GET  /XRay/status/{itemId}                   — check analysis status
/// </summary>
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

    // ------------------------------------------------------------------
    // Query — called by the overlay every few seconds
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the actors visible at the given playback timestamp.
    /// No auth required — the overlay calls this from the browser.
    /// </summary>
    [HttpGet("query")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(XRayQueryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<XRayQueryResponse> Query(
        [FromQuery] Guid itemId,
        [FromQuery] int t)
    {
        if (itemId == Guid.Empty)
            return BadRequest("itemId is required");

        if (!_store.Exists(itemId))
            return NotFound(new { message = "X-Ray data not yet generated for this item." });

        var actors = _store.GetActorsAt(itemId, t);
        return Ok(new XRayQueryResponse(itemId, t, actors));
    }

    // ------------------------------------------------------------------
    // Overlay script — served as JS so the browser can load it
    // ------------------------------------------------------------------

    /// <summary>
    /// Serves the embedded overlay JavaScript file.
    /// </summary>
    [HttpGet("overlay.js")]
    [AllowAnonymous]
    public ContentResult OverlayScript()
    {
        var script = Plugin.Instance?.GetOverlayScript() ?? string.Empty;
        return Content(script, "application/javascript");
    }

    // ------------------------------------------------------------------
    // Analysis trigger — for manual or on-demand use
    // ------------------------------------------------------------------

    /// <summary>
    /// Triggers analysis for a single item in a background task.
    /// Requires user authentication.
    /// </summary>
    [HttpPost("analyze/{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult TriggerAnalysis([FromRoute] Guid itemId, [FromQuery] bool force = false)
    {
        if (itemId == Guid.Empty)
            return BadRequest("itemId is required");

        // Fire and forget — runs in a background thread
        _ = Task.Run(async () =>
        {
            try
            {
                await _xrayService.AnalyzeAsync(itemId, force, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Logged inside AnalyzeAsync
                _ = ex;
            }
        });

        return Accepted(new { itemId, status = "started" });
    }

    // ------------------------------------------------------------------
    // Status
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns whether X-Ray data exists for the given item.
    /// </summary>
    [HttpGet("status/{itemId}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(XRayStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<XRayStatusResponse> Status([FromRoute] Guid itemId)
    {
        var exists = _store.Exists(itemId);
        return Ok(new XRayStatusResponse(itemId, exists));
    }
}

// ------------------------------------------------------------------
// Response DTOs
// ------------------------------------------------------------------

/// <summary>Response model for the query endpoint.</summary>
public record XRayQueryResponse(
    Guid ItemId,
    int T,
    IReadOnlyList<string> Actors);

/// <summary>Response model for the status endpoint.</summary>
public record XRayStatusResponse(
    Guid ItemId,
    bool Ready);
