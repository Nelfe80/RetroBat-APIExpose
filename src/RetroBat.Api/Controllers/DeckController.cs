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
    private readonly DeckLaunchService _lancement;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<ApiExposeOptions> _options;

    public DeckController(DeckStateService deck, DeckLaunchService lancement, Microsoft.Extensions.Options.IOptionsMonitor<ApiExposeOptions> options)
    {
        _deck = deck;
        _lancement = lancement;
        _options = options;
    }

    /// <summary>
    /// L'instantane du Deck (meme contenu que le message <c>deck.snapshot</c>) : jeu lance, score de chaque joueur,
    /// derniere valeur de chaque signal du .MEM, entrees tenues, boutons effectifs, cadre de l'image, cadres demandes.
    /// </summary>
    [HttpGet("snapshot")]
    [ProducesResponseType(typeof(DeckSnapshot), StatusCodes.Status200OK)]
    public ActionResult<DeckSnapshot> Snapshot() => Ok(_deck.Snapshot("rest"));

    /// <summary>
    /// LE SENS DES SIGNAUX D'UN JEU (.SMEM), POUR TOUT JEU ET TOUT CLIENT (API-6, 14/11) : meme contenu que le message
    /// <c>deck.semantics</c>. Avec <c>system</c> et <c>rom</c>, celui de ce jeu, lance ou non. Sans eux, celui du jeu lance
    /// (lu au depart), sinon celui du jeu selectionne dans ES. <c>Status</c> : <c>loaded</c>, <c>absent</c> (pas de .SMEM),
    /// <c>invalid</c> (avec <c>Errors</c>). 404 quand aucun .MEM n'est connu pour le jeu, ou sans jeu. Lecture seule.
    /// </summary>
    [HttpGet("semantics")]
    [ProducesResponseType(typeof(DeckSemantics), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Semantique([FromQuery] string? system, [FromQuery] string? rom, [FromServices] RetroBat.Domain.Models.ApiContext contexte)
    {
        if (!string.IsNullOrWhiteSpace(system) && !string.IsNullOrWhiteSpace(rom))
        {
            return _deck.SemantiqueDuJeu(system, rom) is { } duJeu
                ? Ok(duJeu)
                : NotFound(new { message = "No RAM definition (.MEM) resolved for this game.", system, rom });
        }

        if (_deck.SemantiqueCourante() is { } courante) return Ok(courante);
        var jeu = contexte.Ui.Running ?? contexte.Ui.Selected;
        var brut = jeu is null ? "" : Path.GetFileNameWithoutExtension(!string.IsNullOrEmpty(jeu.GamePath) ? jeu.GamePath : jeu.GameName ?? "");
        if (jeu is null || brut.Length == 0 || string.IsNullOrEmpty(jeu.SystemId))
        {
            return NotFound(new { message = "No running or selected game." });
        }

        return _deck.SemantiqueDuJeu(jeu.SystemId, brut) is { } duCourant
            ? Ok(duCourant)
            : NotFound(new { message = "No RAM definition (.MEM) resolved for this game.", system = jeu.SystemId, rom = brut });
    }

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
        return refus is null ? Ok(new { accepted = true }) : BadRequest(new { accepted = false, reason = refus });
    }

    /// <summary>Retire le cadre garde d'un jeu (systeme et rom vides : tous les cadres gardes).</summary>
    [HttpDelete("frame-plan")]
    public IActionResult Retirer([FromQuery] string? systemId, [FromQuery] string? rom)
    {
        if (!JetonLocal()) return Refus();
        return Ok(new { removed = _deck.Cadres.Retirer(systemId, rom, connexion: null) });
    }

    /// <summary>
    /// Les Decks installes (officiels dans themes/, ceux des joueurs dans decks/) et, pour un jeu (<c>rom</c>), ceux qui lui
    /// conviennent dans l'ordre du choix en jeu.
    /// </summary>
    [HttpGet("installed")]
    public IActionResult Installes([FromQuery] string? rom, [FromQuery] string? romGroup)
    {
        var catalogue = DeckCatalogue.Lire(DeckCatalogue.Dossier(_options.CurrentValue.PlayerDeck.Directory), DeckCatalogue.DossierDataPack);
        var liste = string.IsNullOrWhiteSpace(rom) && string.IsNullOrWhiteSpace(romGroup) ? catalogue.Tous : catalogue.PourLeJeu(rom, romGroup);
        return Ok(liste.Select(e => new { e.Id, e.Name, e.Version, e.Official, e.Default, e.VariantOf, e.Roms }));
    }

    /// <summary>Les choix retenus (joueur, jeu, mode, Deck ou off) et le Deck du lancement en cours.</summary>
    [HttpGet("choices")]
    public IActionResult Choix() => Ok(new { choices = _lancement.Choix.Tous(), launch = _lancement.EnCours });

    private bool JetonLocal() => LocalWriteToken.Matches(Request.Headers[LocalWriteToken.HeaderName].FirstOrDefault());

    private IActionResult Refus() => StatusCode(StatusCodes.Status403Forbidden, new { status = "rejected", reason = "local_token_required", header = LocalWriteToken.HeaderName });
}
