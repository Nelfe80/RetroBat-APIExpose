using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Nodes;
using RetroBat.Api.Replay.Sharing;
using RetroBat.Api.Replay.Social;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Controllers;

/// <summary>
/// NelfeNet — les événements sociaux que cette borne détient (LOT R9 ; CDC §58, §70).
///
/// C'est la porte qui rend le lot distribué plutôt que centralisé : un pair vient chercher ici
/// les réactions d'un replay, les vérifie contre la clé qu'il a épinglée, et n'a besoin ni de
/// nous croire, ni de joindre la plateforme. On sert ce qu'on a reçu, VERBATIM : retoucher un
/// événement en transit reviendrait à casser sa signature, ce qui est précisément le but.
///
/// Même garde que pour les octets : on ne sert les événements que d'un replay dont on servirait
/// l'objet. Un refus répond 404 et non 403 — un demandeur n'a pas à apprendre qu'un replay privé
/// existe (§48), et ses réactions le trahiraient tout autant que ses octets.
/// </summary>
[ApiController]
[Tags("Replay")]
[Route("api/v1/replay/social")]
public sealed class ReplaySocialController : ControllerBase
{
    private readonly ReplaySocialStore _social;
    private readonly ReplayStore _store;
    private readonly ReplaySharePolicy _policy;
    private readonly ILogger<ReplaySocialController> _logger;

    public ReplaySocialController(ReplaySocialStore social, ReplayStore store,
        ReplaySharePolicy policy, ILogger<ReplaySocialController> logger)
    {
        _social = social; _store = store; _policy = policy; _logger = logger;
    }

    /// <summary>Les événements détenus pour ce replay, tels qu'ils sont arrivés.</summary>
    [HttpGet("")]
    [HttpHead("")]
    public IActionResult Get([FromQuery(Name = "replay_id")] string? replayId,
        [FromQuery(Name = "object_sha256")] string? objectSha)
    {
        if (string.IsNullOrWhiteSpace(replayId))
            return BadRequest(new { ok = false, error = new { code = "REPLAY_MANIFEST_INVALID" } });

        var manifest = _store.GetManifest(replayId);
        var sha = manifest?.Object.Sha256 ?? (objectSha ?? string.Empty);
        if (sha.Length != 64) return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });

        var decision = _policy.Evaluate(sha);
        if (!decision.Allowed)
        {
            _logger.LogInformation("Replay social : refus de servir {ReplayId} ({Reason}).", replayId, decision.Reason);
            return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });
        }

        var events = new JsonArray();
        foreach (var e in _social.Read(replayId)) events.Add(e.ToTransport());

        return new JsonResult(new JsonObject
        {
            ["ok"] = true,
            ["schema"] = SocialEventVerifier.Schema,
            ["target_id"] = replayId,
            ["object_sha256"] = sha,
            ["count"] = events.Count,
            ["events"] = events,
        });
    }

    /// <summary>Diagnostic : force une récupération et rend son compte rendu, plutôt que
    /// d'attendre la prochaine lecture pour savoir si la chaîne tient.</summary>
    [HttpPost("fetch")]
    public async Task<IActionResult> Fetch([FromQuery(Name = "replay_id")] string? replayId,
        [FromServices] ReplaySocialFeedService feed, CancellationToken ct)
    {
        if (!IsLocalCaller()) return NotFound();
        var r = await feed.FetchAsync(replayId ?? string.Empty, ct);
        return Ok(new
        {
            fetched = r.Fetched,
            added = r.Added,
            source = r.Source,
            reason = r.Reason,
            held = _social.Count(replayId ?? string.Empty),
        });
    }

    /// <summary>Diagnostic : l'ancre de confiance de cette borne. On ne montre que l'empreinte,
    /// qui suffit à comparer deux machines sans transporter la clé partout.</summary>
    [HttpGet("issuer")]
    public async Task<IActionResult> Issuer([FromServices] SocialIssuerPin pin, CancellationToken ct)
    {
        if (!IsLocalCaller()) return NotFound();
        var epingle = await pin.EnsureAsync(ct);
        return Ok(new { pinned = epingle is not null, key_id = epingle?.KeyId ?? string.Empty });
    }

    private bool IsLocalCaller()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        return remote is null || System.Net.IPAddress.IsLoopback(remote);
    }
}
