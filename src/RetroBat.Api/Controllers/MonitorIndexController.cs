using Microsoft.AspNetCore.Mvc;

namespace RetroBat.Api.Controllers;

/// <summary>
/// L'ANCIENNE SYNCHRONISATION DE L'ECRAN DES EMULATEURS, RETIREE EN 1.9.86 (23/10). APIExpose ecrivait a chaque
/// demarrage <c>&lt;systeme&gt;.MonitorIndex</c> (mame, fbneo, neogeo...) avec le numero GDI de l'ecran d'ES
/// (<c>\\.\DISPLAY7</c> donnait 7). Le lanceur de RetroBat lit desormais cette option comme un rang a partir de 0, ecran
/// principal en premier : le numero GDI, que Windows change quand un ecran se rallume, envoyait les jeux sur un autre
/// ecran, et ecrasait le choix du joueur. C'est en plus une option native d'ES, qu'APIExpose n'ecrit pas. L'adresse
/// reste pour Marquee Manager, qui l'appelle a l'enregistrement de son reglage : elle ne fait plus rien.
/// </summary>
[ApiController]
[Route("api/v1/system/monitor-index")]
public sealed class MonitorIndexController : ControllerBase
{
    [HttpPost("sync")]
    public IActionResult Sync([FromQuery] string? deviceName = null)
        => Ok(new { applied = false, monitorIndex = (int?)null, status = "disabled", reason = "monitor_index_left_to_emulationstation" });
}
