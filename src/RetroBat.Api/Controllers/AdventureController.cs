using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Adventure;

namespace RetroBat.Api.Controllers;

/// <summary>
/// NELFEPLAY ADVENTURE sur la borne (CDC v1.0) : ce que le panneau d'ES lit et commande. Le site est proprietaire des aventures ;
/// ces routes ne font que les relayer avec ce que la borne possede (jeux installes, versions gardees, file d'envoi).
/// </summary>
[ApiController]
[Tags("Adventure")]
[Route("api/v1/adventure")]
public sealed class AdventureController : ControllerBase
{
    private readonly AdventureCollection _collection;
    private readonly IAdventureSite _site;
    private readonly AdventureStockage _stockage;
    private readonly AdventureFileDEnvoi _file;

    public AdventureController(AdventureCollection collection, IAdventureSite site, AdventureStockage stockage, AdventureFileDEnvoi file)
    {
        _collection = collection;
        _site = site;
        _stockage = stockage;
        _file = file;
    }

    /// <summary>La collection, les jeux d'aventure de cette borne et la file d'envoi des versions.</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        collection = _collection.Service.Status,
        games = _collection.Games.Select(jeu => new
        {
            id = jeu.Game.Id,
            name = jeu.Game.Name,
            players = jeu.Game.Players,
            core = jeu.Core?.Core,
            content = jeu.ContentKey,
        }),
        pending_upload = _file.EnAttente.Select(e => new { version = e.Version.Id, adventure = e.Version.Adventure, attempts = e.Essais }),
    });

    /// <summary>Mes aventures et les invitations en attente, ou celles ouvertes a tous avec <c>open=true</c> (de ce jeu si <c>game</c>).</summary>
    [HttpGet("list")]
    public async Task<IActionResult> List([FromQuery] bool open, [FromQuery] string? game, CancellationToken ct)
    {
        if (open)
        {
            var ouvertes = await _site.OuvertesAsync(game, ct);
            return ouvertes is null ? StatusCode(502, new { ok = false, reason = "offline" }) : Ok(new { ok = true, adventures = ouvertes });
        }

        var miennes = await _site.MesAventuresAsync(ct);
        return miennes is null
            ? StatusCode(502, new { ok = false, reason = "offline" })
            : Ok(new { ok = true, adventures = miennes.Aventures, invitations = miennes.Invitations });
    }

    public sealed record CreerDemande(string Game, int Seats, string? Mode, string? Title);

    /// <summary>Cree une aventure sur un jeu installe : son contenu et son coeur sont ceux de cette borne.</summary>
    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] CreerDemande demande, CancellationToken ct)
    {
        var jeu = _collection.Games.FirstOrDefault(g => g.Game.Id == demande.Game);
        if (jeu is null)
        {
            return NotFound(new { ok = false, reason = "game_not_installed" });
        }

        var places = Math.Clamp(demande.Seats, 2, jeu.Game.Players);
        var mode = demande.Mode == AdventureModes.Open ? AdventureModes.Open : AdventureModes.Exclusive;
        var coeur = jeu.Core?.Core;
        var aventure = await _site.CreerAsync(jeu.Game.Id, jeu.ContentKey, places, mode, demande.Title, coeur,
            RetroBat.Api.Speedrun.EtatsParCoeur.EmpreinteDuCoeurInstalle(coeur), ct);
        return aventure is null ? StatusCode(502, new { ok = false, reason = "offline_or_refused" }) : Ok(new { ok = true, adventure = aventure });
    }

    public sealed record SeanceDemande(string Adventure, int Players);

    /// <summary>
    /// Ouvre une seance : le site cree ou rattache la gameroom MULTIPLAYER (l'ouvreur y est place) et le salon vocal de l'aventure ;
    /// la gameroom parait ensuite au sondage MULTIPLAYER, et tout le reste passe par lui.
    /// </summary>
    [HttpPost("open")]
    public async Task<IActionResult> Open([FromBody] SeanceDemande demande, CancellationToken ct)
    {
        if (!AdventureStockage.NomSur(demande.Adventure))
        {
            return BadRequest(new { ok = false, reason = "bad_id" });
        }

        var seance = await _site.OuvrirSeanceAsync(demande.Adventure, Math.Max(1, demande.Players), ct);
        return seance is null ? StatusCode(502, new { ok = false, reason = "offline" })
            : Ok(new { ok = seance.Ok, reason = seance.Reason, attached = seance.Attached, room = seance.Room, resume = seance.Resume, voice = seance.VoiceSupported });
    }

    public sealed record InviterDemande(string Adventure, string Handle);

    /// <summary>Tout participant invite (« qui invite decide ») ; l'exclusivite et l'ouverture sont la regle du site.</summary>
    [HttpPost("invite")]
    public async Task<IActionResult> Invite([FromBody] InviterDemande demande, CancellationToken ct)
    {
        if (!AdventureStockage.NomSur(demande.Adventure) || string.IsNullOrWhiteSpace(demande.Handle))
        {
            return BadRequest(new { ok = false, reason = "bad_request" });
        }

        var (ok, raison) = await _site.InviterAsync(demande.Adventure, demande.Handle.Trim(), ct);
        return ok ? Ok(new { ok = true }) : StatusCode(raison == "offline" ? 502 : 409, new { ok = false, reason = raison });
    }

    public sealed record InvitationDemande(string Invitation, bool Accept);

    /// <summary>Accepter ou refuser une invitation d'aventure (accepter = devenir participant).</summary>
    [HttpPost("invitation")]
    public async Task<IActionResult> Invitation([FromBody] InvitationDemande demande, CancellationToken ct)
        => AdventureStockage.NomSur(demande.Invitation) && await _site.AccepterAsync(demande.Invitation, demande.Accept, ct)
            ? Ok(new { ok = true })
            : StatusCode(409, new { ok = false, reason = "refused_or_offline" });

    /// <summary>Les versions gardees sur cette borne pour une aventure (copie locale de l'hote).</summary>
    [HttpGet("{adventure}/versions")]
    public IActionResult Versions(string adventure)
        => AdventureStockage.NomSur(adventure) ? Ok(new { ok = true, versions = _stockage.Versions(adventure) }) : BadRequest(new { ok = false, reason = "bad_id" });
}
