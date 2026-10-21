using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Multiplayer;

namespace RetroBat.Api.Controllers;

/// <summary>
/// NELFEPLAY MULTIPLAYER sur cette borne : la collection, les gamerooms du joueur, et les gestes du
/// panneau (rejoindre, quitter, inviter, repondre), pour le panneau d'ES comme pour un essai depuis
/// Swagger. Les mots de passe netplay ne sortent jamais par ici.
/// </summary>
[ApiController]
[Tags("Multiplayer")]
[Route("api/v1/multiplayer")]
public sealed class MultiplayerController : ControllerBase
{
    private readonly MultiplayerCollectionSyncService _collection;
    private readonly MultiplayerSessionService _session;

    public MultiplayerController(MultiplayerCollectionSyncService collection, MultiplayerSessionService session)
    {
        _collection = collection;
        _session = session;
    }

    /// <summary>La collection (catalogue, jeux trouves, manquants) et l'etat des gamerooms.</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        collection = _collection.Status,
        games = _collection.Games.Select(jeu => new
        {
            id = jeu.Game.Id,
            name = jeu.Game.Name,
            level = jeu.Game.Level,
            players = jeu.Game.Players,
            core = jeu.Core?.Core,
            content = jeu.ContentKey,
            path = jeu.Path,
        }),
        session = _session.Etat.SansSecrets(),
    });

    public sealed record RejoindreDemande(string Path);

    /// <summary>L'appui long sur un jeu de la collection.</summary>
    [HttpPost("join")]
    public async Task<IActionResult> Join([FromBody] RejoindreDemande demande, CancellationToken ct)
    {
        var refus = await _session.RejoindreAsync(demande.Path, ct);
        return refus is null ? Ok(new { ok = true }) : Conflict(new { ok = false, reason = refus });
    }

    public sealed record RoomDemande(string Room);

    [HttpPost("leave")]
    public async Task<IActionResult> Leave([FromBody] RoomDemande demande, CancellationToken ct)
        => await _session.QuitterAsync(demande.Room, ct) ? Ok(new { ok = true }) : Conflict(new { ok = false });

    public sealed record InviterDemande(string Room, string Handle);

    [HttpPost("invite")]
    public async Task<IActionResult> Invite([FromBody] InviterDemande demande, CancellationToken ct)
    {
        var refus = await _session.InviterAsync(demande.Room, demande.Handle, ct);
        return refus is null ? Ok(new { ok = true }) : Conflict(new { ok = false, reason = refus });
    }

    public sealed record ReponseDemande(string Invitation, bool Accept);

    [HttpPost("invitation")]
    public async Task<IActionResult> Answer([FromBody] ReponseDemande demande, CancellationToken ct)
    {
        var refus = await _session.RepondreAsync(demande.Invitation, demande.Accept, ct);
        return refus is null ? Ok(new { ok = true }) : Conflict(new { ok = false, reason = refus });
    }

    /// <summary>Les amis en ligne (INVITE FRIENDS).</summary>
    [HttpGet("friends")]
    public async Task<IActionResult> Friends(CancellationToken ct) => Ok(await _session.AmisAsync(ct));
}
