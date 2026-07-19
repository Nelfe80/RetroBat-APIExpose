using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Media;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Cabinet badge overlay: a small QR + cabinet number shown bottom-right over
/// the RetroBat screen so players check in from their phone. Driven by the
/// fleet hub: visible on a free cabinet, hidden while a player is checked in.
/// Requires CabinetBadgeOverlay:Enabled in appsettings (venue opt-in).
/// </summary>
[ApiController]
[Tags("System & Health")]
[Route("api/v1/cabinet-badge")]
public sealed class CabinetBadgeController : ControllerBase
{
    private readonly CabinetBadgeOverlayService _overlay;

    public CabinetBadgeController(CabinetBadgeOverlayService overlay)
    {
        _overlay = overlay;
    }

    /// <summary>Current badge state (visible, image URL, label).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(CabinetBadgeOverlayService.BadgeState), StatusCodes.Status200OK)]
    public ActionResult<CabinetBadgeOverlayService.BadgeState> Get() => Ok(_overlay.GetState());

    /// <summary>
    /// Shows, updates or hides the badge. The hub calls it with its own
    /// checkin-qr URL at enrollment, then visible=false on check-in and
    /// visible=true on check-out.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CabinetBadgeOverlayService.BadgeState), StatusCodes.Status200OK)]
    public async Task<ActionResult<CabinetBadgeOverlayService.BadgeState>> Apply(
        [FromBody] CabinetBadgeRequest request,
        CancellationToken cancellationToken)
    {
        await _overlay.ApplyAsync(
            request.Visible, request.ImageUrl, request.Label,
            request.Mode, request.Seed, request.Colors, request.Subtitle,
            cancellationToken);
        return Ok(_overlay.GetState());
    }
}

/// <summary>Mode qr (borne libre) ou player (plaque joueur : avatar pixel
/// dessine localement depuis seed+colors, pseudo, rang en sous-titre).</summary>
public sealed record CabinetBadgeRequest(
    bool Visible,
    string? ImageUrl,
    string? Label,
    string? Mode = null,
    string? Seed = null,
    int? Colors = null,
    string? Subtitle = null);
