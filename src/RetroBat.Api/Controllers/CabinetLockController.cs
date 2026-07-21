using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Media;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Écran de verrouillage de la borne : plein écran, fanart du jeu ou du
/// système en cours en fond, raison en grand (« Borne réservée », « Borne en
/// maintenance »…). Poussé par le hub quand la salle verrouille la borne ;
/// le QR de check-in est masqué pendant ce temps. Même opt-in salle que le
/// badge (CabinetBadgeOverlay:Enabled).
/// </summary>
[ApiController]
[Tags("System & Health")]
[Route("api/v1/cabinet-lock")]
public sealed class CabinetLockController : ControllerBase
{
    private readonly CabinetLockOverlayService _overlay;

    public CabinetLockController(CabinetLockOverlayService overlay)
    {
        _overlay = overlay;
    }

    /// <summary>Current lock screen state (visible, title, subtitle).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(CabinetLockOverlayService.LockState), StatusCodes.Status200OK)]
    public ActionResult<CabinetLockOverlayService.LockState> Get() => Ok(_overlay.GetState());

    /// <summary>Shows or hides the fullscreen lock screen.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(CabinetLockOverlayService.LockState), StatusCodes.Status200OK)]
    public async Task<ActionResult<CabinetLockOverlayService.LockState>> Apply(
        [FromBody] CabinetLockRequest request,
        CancellationToken cancellationToken)
    {
        await _overlay.ApplyAsync(request.Visible, request.Title, request.Subtitle, cancellationToken);
        return Ok(_overlay.GetState());
    }
}

/// <summary>Title = la raison en grand ; Subtitle = la ligne d'explication.</summary>
public sealed record CabinetLockRequest(bool Visible, string? Title = null, string? Subtitle = null);
