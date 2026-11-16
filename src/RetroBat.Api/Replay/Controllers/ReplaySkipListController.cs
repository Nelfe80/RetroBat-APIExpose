using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Replay.Recording;

namespace RetroBat.Api.Replay.Controllers;

/// <summary>
/// LA LISTE DES JEUX QUI NE S'ENREGISTRENT PLUS (16/11, decision user). Un jeu y entre quand RetroArch plante pendant son
/// enregistrement (<see cref="GardeDesPlantages"/>) ; son inscription expire d'elle-meme, et l'outil de diagnostic
/// (bouton « Reessayer l'enregistrement des replays ») la lit et la leve par ces deux routes. En boucle locale seulement :
/// la liste nomme les jeux, les coeurs et le RetroArch de la machine, et la lever ne regarde que son proprietaire.
/// Lever garde l'historique des plantages : si RetroArch replante, le jeu revient pour un delai double.
/// </summary>
[ApiController]
[Tags("Replay")]
[Route("api/v1/replay/skip-list")]
public sealed class ReplaySkipListController : ControllerBase
{
    private readonly GardeDesPlantages _garde;

    public ReplaySkipListController(GardeDesPlantages garde) => _garde = garde;

    private bool IsLocalCaller()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        return remote is null || System.Net.IPAddress.IsLoopback(remote);
    }

    /// <summary>Une inscription, telle que l'outil de diagnostic l'affiche.</summary>
    public sealed record Entree(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("game")] string Game,
        [property: JsonPropertyName("core")] string Core,
        [property: JsonPropertyName("retroarch")] string RetroArch,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("last_crash")] DateTime LastCrash,
        [property: JsonPropertyName("crash_count")] int CrashCount,
        [property: JsonPropertyName("crashes")] IReadOnlyList<DateTime> Crashes,
        [property: JsonPropertyName("next_try")] DateTime NextTry,
        [property: JsonPropertyName("active")] bool Active,
        [property: JsonPropertyName("lifted_at")] DateTime? LiftedAt);

    public sealed record ResetRequest([property: JsonPropertyName("key")] string? Key = null);

    private Entree Vue(GardeDesPlantages.Renoncement r)
        => new(r.Cle, r.Systeme, r.Jeu, r.Coeur, r.RetroArch, VoyantDuReplay.MotifPlantage, r.Le, r.NombreDePlantages,
            r.Plantages is { Count: > 0 } p ? p : new List<DateTime> { r.Le }, _garde.ProchainEssai(r), _garde.Active(r), r.LeveeLe);

    /// <summary>
    /// Les jeux de la liste : actifs (pas d'enregistrement jusqu'a <c>next_try</c>) et passes (historique, deja retentes).
    /// </summary>
    [HttpGet]
    public IActionResult List()
    {
        if (!IsLocalCaller()) return NotFound();
        var items = _garde.Inscriptions().Select(Vue).ToList();
        return Ok(new
        {
            ok = true,
            base_days = _garde.DelaiDeBase.TotalDays,
            active = items.Count(i => i.Active),
            items,
        });
    }

    /// <summary>
    /// Leve les inscriptions actives : toutes, ou celle de <c>key</c> (corps JSON ou parametre). Le jeu s'enregistre de
    /// nouveau des sa prochaine partie ; l'historique de ses plantages reste.
    /// </summary>
    [HttpPost("reset")]
    public async Task<IActionResult> Reset([FromQuery] string? key = null, CancellationToken ct = default)
    {
        if (!IsLocalCaller()) return NotFound();
        // Le corps est facultatif (un POST vide leve tout) : lu a la main, pour qu'un appel sans corps ni type passe. Un corps
        // illisible est refuse : il ne doit pas lever toute la liste quand on visait un seul jeu.
        var cle = string.IsNullOrWhiteSpace(key) ? null : key;
        if (cle is null)
        {
            using var lecteur = new StreamReader(Request.Body);
            var texte = await lecteur.ReadToEndAsync(ct);
            if (!string.IsNullOrWhiteSpace(texte))
            {
                try
                {
                    var req = System.Text.Json.JsonSerializer.Deserialize<ResetRequest>(texte);
                    if (!string.IsNullOrWhiteSpace(req?.Key)) cle = req.Key;
                }
                catch (System.Text.Json.JsonException)
                {
                    return BadRequest(new { ok = false, error = new { code = "SKIP_LIST_BODY_INVALID" } });
                }
            }
        }
        if (cle is not null && _garde.InscriptionPour(cle) is null)
            return NotFound(new { ok = false, error = new { code = "SKIP_LIST_KEY_UNKNOWN" } });
        var levees = _garde.Lever(cle);
        return Ok(new
        {
            ok = true,
            lifted = levees.Count,
            items = levees.Select(Vue).ToList(),
            remaining = _garde.Inscriptions().Count(_garde.Active),
        });
    }
}
