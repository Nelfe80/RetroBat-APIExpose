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
    private readonly ReplayVideoPublisher _publication;
    private readonly DirectDuRecordService _direct;
    private readonly DirectPartageService _partage;

    public ReplayVideoController(ReplayVideoRenderer rendu, ReplayVideoPublisher publication, DirectDuRecordService direct,
        DirectPartageService partage)
    {
        _rendu = rendu;
        _publication = publication;
        _direct = direct;
        _partage = partage;
    }

    public sealed record EssaiDuDirectRequest(
        [property: JsonPropertyName("enabled")] bool Actif,
        [property: JsonPropertyName("threshold")] long? Seuil);

    /// <summary>
    /// L'essai du direct d'un record (CDC video V2, §5.8), jusqu'au prochain demarrage de l'API : le prochain jeu lance
    /// ouvre un direct non repertorie, annonce au seul joueur, quand son score passe <c>threshold</c>.
    /// </summary>
    [HttpPost("live-test")]
    public IActionResult LiveTest([FromBody] EssaiDuDirectRequest? req)
    {
        if (!IsLocalCaller()) return NotFound();
        _direct.Essayer(req?.Actif ?? false, req?.Seuil);
        return Ok(new { test = req?.Actif ?? false, threshold = req?.Seuil });
    }

    public sealed record EssaiDuPartageRequest([property: JsonPropertyName("enabled")] bool Actif);

    /// <summary>
    /// L'essai du direct partage (CDC video V2.1, §5.2), jusqu'au prochain demarrage de l'API : les parties suivantes se
    /// portent candidates en passages d'essai (directs non repertories, entre bornes en essai). Toujours desarmer apres.
    /// </summary>
    [HttpPost("shared-test")]
    public IActionResult SharedTest([FromBody] EssaiDuPartageRequest? req)
    {
        if (!IsLocalCaller()) return NotFound();
        _partage.Essayer(req?.Actif ?? false);
        return Ok(new { test = req?.Actif ?? false });
    }

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

    public sealed record PublicationRequest([property: JsonPropertyName("limit")] int? Limite);

    /// <summary>Filme et envoie les records de cette borne que le serveur demande (au plus <c>limit</c>).</summary>
    [HttpPost("publish")]
    public async Task<IActionResult> Publish([FromBody] PublicationRequest? req, CancellationToken ct)
    {
        if (!IsLocalCaller()) return NotFound();
        var r = await _publication.PublierAsync(Math.Max(0, req?.Limite ?? 1), ct).ConfigureAwait(false);
        return Ok(new { targets = r.Cibles, rendered = r.Rendus, sent = r.Envoyees, details = r.Details });
    }
}
