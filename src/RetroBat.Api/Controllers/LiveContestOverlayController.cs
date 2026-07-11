using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Surimpression Live Contest au-dessus du jeu (fenetre topmost bas-droit).
/// Pilotee en LOCAL par le client de jeu Live Contest (page /play de la
/// plateforme) : decompte, GO, bravo final avec le score.
/// </summary>
[ApiController]
[Route("api/v1/overlay")]
public class LiveContestOverlayController : ControllerBase
{
    private readonly LiveContestOverlayService _overlay;

    public LiveContestOverlayController(LiveContestOverlayService overlay)
    {
        _overlay = overlay;
    }

    /// <summary>
    /// Shows (or updates) the Live Contest overlay window above the game.
    /// </summary>
    /// <remarks>
    /// Example:
    ///
    ///     POST /api/v1/overlay/livecontest
    ///     {
    ///       "text": "GO !!!",
    ///       "sub": "Premier a 10 anneaux",
    ///       "durationMs": 4000
    ///     }
    ///
    /// Omit <c>durationMs</c> (or send 0) to keep the window visible until
    /// the next call or a DELETE.
    /// </remarks>
    /// <response code="200">Overlay updated.</response>
    /// <response code="400">Missing text.</response>
    [HttpPost("livecontest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Show([FromBody] OverlayPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Text))
        {
            return BadRequest(new { message = "You must provide the overlay text." });
        }

        if (payload.Center)
        {
            _overlay.ShowCenter(payload.Text.Trim(), payload.Sub, payload.DurationMs);
        }
        else
        {
            _overlay.Show(payload.Title, payload.Text.Trim(), payload.Sub, payload.DurationMs);
        }

        return Ok(new { status = "shown" });
    }

    /// <summary>Hides the Live Contest overlay window.</summary>
    /// <response code="200">Overlay hidden.</response>
    [HttpDelete("livecontest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult HideOverlay()
    {
        _overlay.Hide();
        return Ok(new { status = "hidden" });
    }
}

public class OverlayPayload
{
    /// <summary>Small header line; defaults to "LIVE CONTEST".</summary>
    public string? Title { get; set; }

    /// <summary>Main line (large text).</summary>
    /// <example>GO !!!</example>
    public string Text { get; set; } = string.Empty;

    /// <summary>Secondary line under the main text.</summary>
    /// <example>Premier a 10 anneaux</example>
    public string? Sub { get; set; }

    /// <summary>Centered stage mode (big text) instead of the corner window.</summary>
    public bool Center { get; set; }

    /// <summary>Auto-hide delay in milliseconds; 0 or null keeps it visible.</summary>
    /// <example>4000</example>
    public int? DurationMs { get; set; }
}
