using System.Runtime.Versioning;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Controllers;

/// <summary>
/// LE CONTRAT DU SPEEDRUNHUD (prototype Sonic, §11 bis.5) : snapshot de session, lancement type d'un defi et
/// commandes. Les evenements passent par le WebSocket existant, flux « speedrun » (ws://127.0.0.1:12345/ws/speedrun),
/// qui rejoue le dernier snapshot a la connexion.
///
/// Toute commande demande le jeton local (en-tete X-ApiExpose-Local, fichier state/apiexpose/local-write-token.txt) :
/// le HUD le lit sur le disque, une page web ne le peut pas. Aucune commande par un GET.
/// </summary>
[ApiController]
[Tags("Speedrun")]
[Route("api/v1/speedrun")]
[SupportedOSPlatform("windows")]
public sealed class SpeedrunController : ControllerBase
{
    private readonly SpeedrunSessionService _session;
    private readonly IGamelistStore _gamelists;
    private readonly RomCanonicalResolver _canonical;
    private readonly CatalogueDesDefis _catalogue;

    public SpeedrunController(SpeedrunSessionService session, IGamelistStore gamelists, RomCanonicalResolver canonical, CatalogueDesDefis catalogue)
    {
        _catalogue = catalogue;
        _session = session;
        _gamelists = gamelists;
        _canonical = canonical;
    }

    /// <summary>Les defis ouverts sur cette borne.</summary>
    [HttpGet("challenges")]
    public IActionResult Defis() => Ok(_catalogue.Tous.Select(r => new
    {
        challengeId = r.ChallengeId,
        version = r.Version,
        kind = r.Kind,
        systemId = r.SystemId,
        romGroup = r.RomGroup,
        startPolicy = r.StartPolicy,
        official = r.ChronoOfficiel,
        cores = r.Coeurs,
        rate = r.Cadence,
    }));

    /// <summary>Le snapshot de la session en cours (§11 bis.5) ; 404 sans session.</summary>
    [HttpGet("session")]
    public IActionResult Session() => _session.Instantane() is { } s ? Ok(s) : NotFound(new { active = false });

    public sealed record DemandeDeLancement(string? ChallengeId);

    /// <summary>Lance un defi : contexte type speedrun, coeur du reglement, RetroAchievements coupe pour la seance, aucun score au World Scoring.</summary>
    [HttpPost("launch")]
    public IActionResult Lancer([FromBody] DemandeDeLancement demande)
    {
        if (!JetonLocal()) return Refus();
        var r = _session.Lancer(demande.ChallengeId, (systeme, groupe) => RomDuGroupe.Trouver(_gamelists, _canonical, systeme, groupe));
        if (r.Lance) return Accepted(new { status = "launching", challengeId = demande.ChallengeId });
        return r.Raison switch
        {
            "unknown_challenge" or "not_installed" or "mem_missing" => NotFound(new { status = "rejected", reason = r.Raison, detail = r.Detail }),
            "game_running" => Conflict(new { status = "rejected", reason = r.Raison }),
            "launcher_failed" => StatusCode(StatusCodes.Status502BadGateway, new { status = "rejected", reason = r.Raison }),
            _ => UnprocessableEntity(new { status = "rejected", reason = r.Raison, detail = r.Detail }),
        };
    }

    public sealed record ArgumentsDeCommande(bool? Confirm, string? Comparison);

    public sealed record DemandeDeCommande(string? RequestId, string? SessionId, string? Action, ArgumentsDeCommande? Arguments);

    /// <summary>
    /// Une commande du HUD : run.restart, run.abort, comparison.set. Reponse accepted, rejected ou pending, avec la
    /// raison, l'etat decide et la sequence. Idempotente par requestId ; refusee si sessionId est perime.
    /// </summary>
    [HttpPost("commands")]
    public async Task<IActionResult> Commander([FromBody] DemandeDeCommande demande)
    {
        if (!JetonLocal()) return Refus();
        if (string.IsNullOrWhiteSpace(demande.RequestId) || string.IsNullOrWhiteSpace(demande.Action))
            return BadRequest(new { status = "rejected", reason = "requestId_and_action_required" });
        var reponse = await _session.CommanderAsync(new CommandeSpeedrun(
            demande.RequestId.Trim(), demande.SessionId?.Trim() ?? "", demande.Action.Trim(),
            demande.Arguments?.Confirm == true, demande.Arguments?.Comparison?.Trim())).ConfigureAwait(false);
        return Ok(reponse);
    }

    private bool JetonLocal() => LocalWriteToken.Matches(Request.Headers[LocalWriteToken.HeaderName].FirstOrDefault());

    private IActionResult Refus() => StatusCode(StatusCodes.Status403Forbidden, new { status = "rejected", reason = "local_token_required", header = LocalWriteToken.HeaderName });
}
