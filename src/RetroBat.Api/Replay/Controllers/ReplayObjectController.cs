using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using RetroBat.Api.Replay.Sharing;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Controllers;

/// <summary>
/// NelfeNet — le seul point par lequel un objet quitte cette borne. On y demande un CONTENU par
/// son hash, jamais un fichier par son chemin : l'entrée est validée comme 64 caractères hex, donc
/// aucune chaîne de l'appelant ne peut devenir un chemin.
///
/// Toute la décision est dans <see cref="ReplaySharePolicy"/>. Un refus répond 404 et non 403 :
/// un demandeur n'a pas à apprendre qu'un objet privé existe (CDC §48). La raison du refus reste
/// dans le journal local.
///
/// L'API n'écoute qu'en loopback par défaut ; servir de vrais pairs demande, EN PLUS, d'ouvrir
/// l'écoute (configuration `Urls`), qui est une décision distincte et explicite.
/// </summary>
[ApiController]
[Tags("Replay")]
[Route("api/v1/object")]
public sealed class ReplayObjectController : ControllerBase
{
    private static readonly Regex Sha256Hex = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    private readonly ReplaySharePolicy _policy;
    private readonly IReplayObjectStore _objects;
    private readonly ILogger<ReplayObjectController> _logger;

    public ReplayObjectController(ReplaySharePolicy policy, IReplayObjectStore objects, ILogger<ReplayObjectController> logger)
    {
        _policy = policy; _objects = objects; _logger = logger;
    }

    /// <summary>Sert l'objet adressé par ce SHA-256, s'il est enregistré, public et partageable.</summary>
    [HttpGet("{sha256}")]
    [HttpHead("{sha256}")]
    public async Task<IActionResult> Get(string sha256, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sha256) || !Sha256Hex.IsMatch(sha256))
            return BadRequest(new { ok = false, error = new { code = "OBJECT_ID_INVALID" } });

        var sha = sha256.ToLowerInvariant();
        var decision = _policy.Evaluate(sha);
        if (!decision.Allowed)
        {
            _logger.LogInformation("Replay share : refus de {Sha} ({Reason}).", sha[..8], decision.Reason);
            return NotFound(new { ok = false, error = new { code = "OBJECT_UNAVAILABLE" } });
        }

        // ETag = le hash : un pair qui l'a déjà n'a aucune raison de le retélécharger, et
        // l'identité du contenu est vérifiable avant même d'avoir lu le corps.
        // Compresse au repos : un pair recoit le BRUT, que toutes les versions savent lire.
        var brut = await _objects.EnsureRawAsync(sha, ct).ConfigureAwait(false);
        if (brut is null) return NotFound(new { ok = false, error = new { code = "OBJECT_UNAVAILABLE" } });
        return PhysicalFile(brut, "application/octet-stream",
            lastModified: null, entityTag: new EntityTagHeaderValue('"' + sha + '"'), enableRangeProcessing: true);
    }
}
