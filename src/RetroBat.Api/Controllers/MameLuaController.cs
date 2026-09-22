using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Controllers;

/// <summary>
/// Diagnostic du pont MAME Lua ingame : sessions et compteurs de
/// declenchement par adresse. Consomme par le banc de validation des .MEM
/// arcade (tools/mem-curator/mame_batch_validate.py).
/// </summary>
[ApiController]
[Tags("Game Events")]
[Route("api/v1/mamelua")]
public class MameLuaController : ControllerBase
{
    private readonly MameLuaIngameProvider? _provider;

    public MameLuaController(IEnumerable<IProvider> providers)
    {
        _provider = providers.OfType<MameLuaIngameProvider>().FirstOrDefault();
    }

    /// <summary>Diagnostic snapshot of the MAME Lua ingame bridge sessions (501 when the provider is off).</summary>
    [HttpGet("sessions")]
    public IActionResult Sessions()
        => _provider is null
            ? StatusCode(StatusCodes.Status501NotImplemented)
            : Ok(_provider.SessionsSnapshot());

    /// <summary>Arme la découverte MAME (MEM Explorer) : au prochain HELLO d'un plugin Lua, APIExpose
    /// lui répond <c>DISCOVER|host|port|hz</c> → il streame la RAM principale vers le listener TCP de
    /// l'Explorer (au lieu d'écouter des adresses connues).</summary>
    [HttpPost("discovery/arm")]
    public IActionResult ArmDiscovery([FromBody] DiscoveryArmRequest? req)
    {
        if (_provider is null) return StatusCode(StatusCodes.Status501NotImplemented);
        if (req is null || req.Port is <= 0 or >= 65536)
            return BadRequest(new { error = "port requis (1..65535)." });

        var host = string.IsNullOrWhiteSpace(req.Host) ? "127.0.0.1" : req.Host!.Trim();
        var hz = req.Hz is > 0 and <= 60 ? req.Hz : 8;
        _provider.ArmDiscovery(host, req.Port, hz);
        return Ok(new { ok = true, armed = true, host, port = req.Port, hz });
    }

    /// <summary>Désarme la découverte MAME (retour au mode WATCH runtime).</summary>
    [HttpDelete("discovery/arm")]
    public IActionResult DisarmDiscovery()
    {
        if (_provider is null) return StatusCode(StatusCodes.Status501NotImplemented);
        _provider.DisarmDiscovery();
        return Ok(new { ok = true, armed = false });
    }

    // ── Le labo pilote MAME ────────────────────────────────────────────────────────────────
    //
    // Ces routes n'existent que pour NelfeScoreLab, qui doit qualifier une definition sur
    // MAME standalone comme il le fait sur RetroArch : RetroArch a une manette reseau, MAME
    // n'en a pas, alors le plugin Lua force les champs d'entree a la demande. Elles ne font
    // rien hors partie (409) : il n'y a personne au bout.

    /// <summary>Force (pressed=true) ou rend (pressed=false) un champ d'entree MAME par son nom (« 1 Player Start », « Coin 1 », « P1 Up », « P1 Button 1 »).</summary>
    [HttpPost("input")]
    public async Task<IActionResult> Input([FromBody] InputRequest? req, CancellationToken ct)
    {
        if (_provider is null) return StatusCode(StatusCodes.Status501NotImplemented);
        if (req is null || string.IsNullOrWhiteSpace(req.Field)) return BadRequest(new { error = "field requis." });
        if (!_provider.EstConnecte) return Conflict(new { error = "aucune partie MAME en cours." });
        var ok = await _provider.RequestInputAsync(req.Field.Trim(), req.Pressed, ct);
        return ok ? Accepted(new { ok = true, field = req.Field.Trim(), pressed = req.Pressed }) : Conflict(new { error = "MAME ne repond pas." });
    }

    /// <summary>Rend tous les champs forces a la machine.</summary>
    [HttpPost("input/release")]
    public async Task<IActionResult> Release(CancellationToken ct)
    {
        if (_provider is null) return StatusCode(StatusCodes.Status501NotImplemented);
        if (!_provider.EstConnecte) return Conflict(new { error = "aucune partie MAME en cours." });
        return await _provider.RequestReleaseAsync(ct) ? Accepted(new { ok = true }) : Conflict(new { error = "MAME ne repond pas." });
    }

    /// <summary>Les noms des champs d'entree de la machine en cours.</summary>
    [HttpGet("inputs")]
    public async Task<IActionResult> Inputs(CancellationToken ct)
    {
        if (_provider is null) return StatusCode(StatusCodes.Status501NotImplemented);
        if (!_provider.EstConnecte) return Conflict(new { error = "aucune partie MAME en cours." });
        var champs = await _provider.RequestInputsAsync(TimeSpan.FromSeconds(2), ct);
        return Ok(new { ok = true, fields = champs });
    }

    /// <summary>Demande une capture a MAME et rend le fichier PNG apparu (le tampon du jeu, a sa definition d'origine).</summary>
    [HttpPost("snapshot")]
    public async Task<IActionResult> Snapshot(CancellationToken ct)
    {
        if (_provider is null) return StatusCode(StatusCodes.Status501NotImplemented);
        if (!_provider.EstConnecte) return Conflict(new { error = "aucune partie MAME en cours." });
        var avant = DateTime.UtcNow.AddSeconds(-1);
        if (!await _provider.RequestSnapshotAsync(ct)) return Conflict(new { error = "MAME ne repond pas." });
        var dossiers = new[]
        {
            Path.Combine(RetroBat.Domain.Paths.RetroBatPaths.RetroBatRoot, "screenshots"),
            Path.Combine(RetroBat.Domain.Paths.RetroBatPaths.RetroBatRoot, "emulators", "mame", "snap"),
        };
        var limite = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < limite)
        {
            foreach (var dossier in dossiers)
            {
                if (!Directory.Exists(dossier)) continue;
                try
                {
                    var candidat = new DirectoryInfo(dossier)
                        .EnumerateFiles("*.png", SearchOption.AllDirectories)
                        .Where(f => f.LastWriteTimeUtc >= avant && f.Length > 0)
                        .OrderByDescending(f => f.LastWriteTimeUtc)
                        .FirstOrDefault();
                    if (candidat is not null) return Ok(new { ok = true, file = candidat.FullName });
                }
                catch (IOException) { /* le dossier bouge : on retente */ }
            }
            await Task.Delay(150, ct);
        }
        return Ok(new { ok = false, file = (string?)null });
    }

    public sealed class InputRequest
    {
        public string? Field { get; set; }
        public bool Pressed { get; set; } = true;
    }

    public sealed class DiscoveryArmRequest
    {
        public string? Host { get; set; }
        public int Port { get; set; }
        public int Hz { get; set; }
    }
}
