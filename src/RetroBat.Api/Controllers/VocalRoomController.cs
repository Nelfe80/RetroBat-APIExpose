using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.VocalRoom;

namespace RetroBat.Api.Controllers;

/// <summary>
/// LE VOCAL DISCORD DES PARTIES MULTIPLAYER (lot B, 12/11/2026) : son etat et les reglages micro et casque, pour
/// l'interface locale et le diagnostic. Aucune route ne cree de conversation, ne choisit de lobby ni ne lit un jeton :
/// le site decide, la borne execute.
/// </summary>
[ApiController]
[Route("api/v1/vocalroom")]
public sealed class VocalRoomController : ControllerBase
{
    private readonly VocalRoomProcessService _processus;
    private readonly VocalRoomCoordinator _coordinateur;
    private readonly DiscordPresenceCoordinator _presence;

    public VocalRoomController(VocalRoomProcessService processus, VocalRoomCoordinator coordinateur, DiscordPresenceCoordinator presence)
    {
        _processus = processus;
        _coordinateur = coordinateur;
        _presence = presence;
    }

    /// <summary>L'etat du vocal : processus, etat observe par l'exe, indicateur du panneau. Sans secret.</summary>
    [HttpGet("status")]
    public IActionResult Status() => Ok(new
    {
        exe = _processus.Etat.PourLeBus(),
        indicator = _coordinateur.Affichage is { } a
            ? new { state = a.Etat.ToString(), participants = a.Participants, mic_muted = a.MicroCoupe, deafened = a.CasqueCoupe, reason = a.Raison }
            : null,
        presence = new { enabled = DiscordPresenceCoordinator.Active, owner = _presence.Proprietaire.ToString() },
    });

    public sealed record ReglageDemande(bool? Muted, bool? Deafened);

    /// <summary>Couper ou rouvrir le micro, couper ou rendre le son du vocal. Sans effet sans session vocale.</summary>
    [HttpPost("settings")]
    public async Task<IActionResult> Settings([FromBody] ReglageDemande demande)
    {
        await _coordinateur.ReglerAsync(demande.Muted, demande.Deafened).ConfigureAwait(false);
        return Ok(new { mic_muted = _coordinateur.MicroCoupe, deafened = _coordinateur.CasqueCoupe });
    }
}
