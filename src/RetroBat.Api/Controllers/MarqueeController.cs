using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Media;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Marquee Manager actions: on-demand marquee/DMD generation for the current
/// game context.
/// </summary>
[ApiController]
[Tags("Marquee Manager")]
[Route("api/v1/marquee")]
public sealed class MarqueeController : ControllerBase
{
    private readonly ApiContext _context;
    private readonly IMediaPrefetchService _mediaPrefetchService;
    private readonly MarqueeAutogenService _marqueeAutogenService;

    public MarqueeController(
        ApiContext context,
        IMediaPrefetchService mediaPrefetchService,
        MarqueeAutogenService marqueeAutogenService)
    {
        _context = context;
        _mediaPrefetchService = mediaPrefetchService;
        _marqueeAutogenService = marqueeAutogenService;
    }

    /// <summary>
    /// Generates the custom marquee/DMD for the current game as if it had just
    /// been selected (same rules: only when no real marquee exists, profile
    /// from the MARQUEE AUTOGEN option).
    /// </summary>
    /// <response code="200">Generation result (Generated, SkippedReason, output paths).</response>
    /// <response code="404">No game selected or running.</response>
    [HttpPost("autogen")]
    [ProducesResponseType(typeof(MarqueeAutogenResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MarqueeAutogenResult>> GenerateForCurrentGame(CancellationToken cancellationToken)
    {
        var game = _context.Ui.Running ?? _context.Ui.Selected;
        if (game is null)
        {
            return NotFound(new { message = "No game selected or running." });
        }

        try
        {
            var plan = await _mediaPrefetchService.PrepareLocalProjectionPlanAsync(game, cancellationToken);
            var result = await _marqueeAutogenService.GenerateForSelectedGameAsync(plan, cancellationToken);
            return Ok(result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status409Conflict, new { error = ex.Message });
        }
    }
}
