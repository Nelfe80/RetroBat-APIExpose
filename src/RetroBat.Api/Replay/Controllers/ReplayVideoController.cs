using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Replay.Video;

namespace RetroBat.Api.Replay.Controllers;

/// <summary>
/// Le rendu video d'un replay, a la demande et depuis la borne seulement (CDC video V2, §4). C'est la piece que
/// la file de rendu appellera au repos ; la route sert aux essais et au diagnostic.
/// </summary>
[ApiController]
[Route("api/v1/replay/video")]
public sealed class ReplayVideoController : ControllerBase
{
    private readonly ReplayVideoRenderer _rendu;

    public ReplayVideoController(ReplayVideoRenderer rendu) { _rendu = rendu; }

    private bool IsLocalCaller()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        return remote is null || System.Net.IPAddress.IsLoopback(remote);
    }

    /// <summary>Le binding de l'application est en camelCase : les noms du corps sont donnes ici.</summary>
    public sealed record RenduRequest(
        [property: JsonPropertyName("replay_id")] string? ReplayId,
        [property: JsonPropertyName("run_start_frame")] long? Debut,
        [property: JsonPropertyName("run_end_frame")] long? Fin);

    [HttpPost("render")]
    public async Task<IActionResult> Render([FromBody] RenduRequest? req, CancellationToken ct)
    {
        if (!IsLocalCaller()) return NotFound();
        if (string.IsNullOrWhiteSpace(req?.ReplayId)) return BadRequest(new { error = "replay_id" });
        var r = await _rendu.RendreAsync(req.ReplayId.Trim(), req.Debut, req.Fin, ct).ConfigureAwait(false);
        return Ok(new
        {
            ok = r.Ok,
            reason = r.Raison,
            file = r.Fichier,
            run_start_frame = r.Debut,
            run_end_frame = r.Fin,
            reached_frame = r.FrameAtteinte,
            seconds = Math.Round(r.Secondes, 1),
        });
    }
}
