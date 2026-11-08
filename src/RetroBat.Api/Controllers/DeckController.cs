using System.Runtime.Versioning;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Controllers;

/// <summary>
/// L'etat du jeu pour le PlayerDeck, en REST, a cote du WebSocket (<c>/ws/deck</c>, ou le flux global <c>/ws</c>) :
/// l'instantane, et les cadres que ses themes demandent. Les ecritures demandent le jeton local
/// (en-tete X-ApiExpose-Local), comme les commandes du speedrun.
/// </summary>
[ApiController]
[SupportedOSPlatform("windows")]
[Tags("PlayerDeck")]
[Route("api/v1/deck")]
public sealed class DeckController : ControllerBase
{
    private readonly DeckStateService _deck;

    public DeckController(DeckStateService deck) => _deck = deck;

    /// <summary>
    /// L'instantane du Deck (meme contenu que le message <c>deck.snapshot</c>) : jeu lance, score de chaque joueur,
    /// derniere valeur de chaque signal du .MEM, entrees tenues, boutons effectifs, cadre de l'image, cadres demandes.
    /// </summary>
    [HttpGet("snapshot")]
    [ProducesResponseType(typeof(DeckSnapshot), StatusCodes.Status200OK)]
    public ActionResult<DeckSnapshot> Snapshot() => Ok(_deck.Snapshot("rest"));

    /// <summary>Les cadres demandes (ceux d'un Deck connecte et ceux gardes).</summary>
    [HttpGet("frame-plan")]
    public IActionResult Plans() => Ok(_deck.Cadres.Tous());

    /// <summary>
    /// Garde un cadre pour un jeu (ou pour tous, systeme et rom vides) jusqu'a son retrait. En pixels physiques
    /// relatifs a la zone cliente de RetroArch. 400 avec le motif si le cadre n'est pas recevable.
    /// </summary>
    [HttpPut("frame-plan")]
    public IActionResult Poser([FromBody] DeckFramePlan plan)
    {
        if (!JetonLocal()) return Refus();
        var refus = _deck.Cadres.Poser(plan with { Persist = true, RequestedAtUtc = DateTime.UtcNow }, connexion: null);
        return refus is null ? Ok(new { accepted = true, sessionConfig = DeckStateService.FichierDeSession }) : BadRequest(new { accepted = false, reason = refus });
    }

    /// <summary>Retire le cadre garde d'un jeu (systeme et rom vides : tous les cadres gardes).</summary>
    [HttpDelete("frame-plan")]
    public IActionResult Retirer([FromQuery] string? systemId, [FromQuery] string? rom)
    {
        if (!JetonLocal()) return Refus();
        return Ok(new { removed = _deck.Cadres.Retirer(systemId, rom, connexion: null) });
    }

    /// <summary>
    /// La configuration de session qui imposerait le cadre au lancement en cours (texte a ajouter avec
    /// <c>--appendconfig</c>), 204 s'il n'y en a pas. Elle porte toujours <c>config_save_on_exit = "false"</c>.
    /// </summary>
    [HttpGet("frame-plan/session")]
    public IActionResult Session()
        => DeckStateService.SessionEnVigueur() is { } texte ? Content(texte, "text/plain") : NoContent();

    private bool JetonLocal() => LocalWriteToken.Matches(Request.Headers[LocalWriteToken.HeaderName].FirstOrDefault());

    private IActionResult Refus() => StatusCode(StatusCodes.Status403Forbidden, new { status = "rejected", reason = "local_token_required", header = LocalWriteToken.HeaderName });
}
