using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Media;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Annonce de challenge sur l'écran de la borne (fenêtre 80 % centrée :
/// fanart, logo, objectif, « Tenez-vous prêt ! », compte à rebours, QR de
/// participation). Poussée par le hub de salle avant le coup d'envoi —
/// route volontairement HORS du préfixe /api/v1/overlay (loopback-only).
/// </summary>
[ApiController]
[Tags("Cabinet Overlays")]
[Route("api/v1/challenge-announce")]
public sealed class ChallengeAnnounceController : ControllerBase
{
    private readonly ChallengeAnnounceOverlayService _overlay;

    public ChallengeAnnounceController(ChallengeAnnounceOverlayService overlay)
    {
        _overlay = overlay;
    }

    /// <summary>État courant de l'annonce.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ChallengeAnnounceOverlayService.AnnounceState), StatusCodes.Status200OK)]
    public IActionResult GetState() => Ok(_overlay.GetState());

    /// <summary>Affiche (ou met à jour) l'annonce. Les médias (fanart, logo)
    /// sont résolus localement depuis le gamelist à partir de gamePath.</summary>
    /// <response code="202">Annonce appliquée.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Apply([FromBody] ChallengeAnnounceRequest request, CancellationToken cancellationToken)
    {
        await _overlay.ApplyAsync(
            request.Visible ?? true,
            request.GamePath,
            request.GameName,
            request.Objective,
            request.StartsAtUtc,
            request.QrImageUrl,
            cancellationToken);
        return Accepted(_overlay.GetState());
    }

    /// <summary>Retire l'annonce.</summary>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Hide(CancellationToken cancellationToken)
    {
        await _overlay.ApplyAsync(false, null, null, null, null, null, cancellationToken);
        return Accepted(_overlay.GetState());
    }
}

public sealed record ChallengeAnnounceRequest(
    bool? Visible,
    string? GamePath,
    string? GameName,
    string? Objective,
    DateTime? StartsAtUtc,
    string? QrImageUrl);
