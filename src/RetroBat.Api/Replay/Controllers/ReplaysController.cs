using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Sharing;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Controllers;

/// <summary>
/// API Replay : lecture seule. Renvoie des identités logiques, jamais un chemin local.
/// L'index est une vue dérivée reconstructible depuis les manifests.
///
/// ⚠️ DEUX PUBLICS, DEUX SURFACES. En boucle locale (l'UI de la borne, le panel, le site
/// appairé) tout est visible : c'est la machine du propriétaire. Depuis le RÉSEAU, un appelant
/// ne voit QUE ce qu'il aurait le droit de récupérer, c'est-à-dire les replays que la politique
/// de partage laisserait servir. Sans cette règle, confier la clé d'API à une borne pour qu'elle
/// récupère un objet public lui donnerait aussi la liste et les manifestes de tous les replays
/// PRIVÉS, ce qui viderait la visibilité de son sens (CDC §48).
/// </summary>
[ApiController]
[Tags("Replay")]
[Route("api/v1/[controller]")]
public sealed class ReplaysController : ControllerBase
{
    private readonly ReplayStore _store;
    private readonly ReplayNetworkStateService _network;
    private readonly ReplaySharePolicy _policy;

    public ReplaysController(ReplayStore store, ReplayNetworkStateService network, ReplaySharePolicy policy)
    {
        _store = store; _network = network; _policy = policy;
    }

    /// <summary>Appel depuis la machine elle-même ? Sinon, c'est un pair, et il voit moins.</summary>
    private bool IsLocalCaller()
    {
        var remote = HttpContext.Connection.RemoteIpAddress;
        return remote is null || System.Net.IPAddress.IsLoopback(remote);
    }

    /// <summary>Cet appelant a-t-il le droit de connaître ce replay ?</summary>
    private bool MaySee(string objectSha256) => IsLocalCaller() || _policy.Evaluate(objectSha256).Allowed;

    /// <summary>Liste les replays connus (depuis l'index, reconstruit s'il est absent).</summary>
    [HttpGet]
    public IActionResult List([FromQuery] string? game_id = null)
    {
        var index = _store.ReadIndex();
        if (index.Count == 0) index = _store.RebuildIndex();

        var items = index
            .Where(e => game_id is null || string.Equals(e.GameId, game_id, StringComparison.Ordinal))
            .Where(e => MaySee(e.ObjectSha256))
            .Select(e =>
            {
                var meta = _store.GetMeta(e.ReplayId);
                return new
                {
                    replay_id = e.ReplayId,
                    game_id = e.GameId,
                    created_at = e.CreatedAt,
                    object_sha256 = e.ObjectSha256,
                    visibility = meta?.Visibility ?? "private",
                    // « manifeste connu » et « objet réellement là » sont DEUX choses (CDC §86).
                    local_available = _store.HasObject(e.ObjectSha256),
                    network_state = ReplayNetworkStateService.Wire(_network.Evaluate(e.ReplayId, e.ObjectSha256)),
                    pinned = meta?.Pinned ?? false,
                    score_ref = meta?.ScoreRef,
                };
            })
            .ToList();

        return Ok(new { items, total = items.Count });
    }

    /// <summary>Vue combinée manifeste + métadonnées locales + disponibilité (sans chemin).</summary>
    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var m = _store.GetManifest(id);
        if (m is null || !MaySee(m.Object.Sha256))
            return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });
        return Ok(new
        {
            manifest = m,
            metadata = _store.GetMeta(id),
            local_available = _store.HasObject(m.Object.Sha256),
            network_state = ReplayNetworkStateService.Wire(_network.Evaluate(id, m.Object.Sha256)),
        });
    }

    /// <summary>Le manifeste immuable, tel qu'écrit sur disque (snake_case).</summary>
    [HttpGet("{id}/manifest")]
    public IActionResult GetManifest(string id)
    {
        // C'est par ce point qu'un pair apprend comment jouer un replay qu'il n'a jamais vu :
        // core, ROM par crc32, cadence, repères de frames. Il ne doit donc sortir que pour un
        // replay que cette borne accepte de partager.
        var m = _store.GetManifest(id);
        if (m is null || !MaySee(m.Object.Sha256))
            return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });
        var path = _store.ManifestPath(id);
        if (!System.IO.File.Exists(path))
            return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });
        return new FileContentResult(System.IO.File.ReadAllBytes(path), "application/json");
    }

    public sealed record VisibilityRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("visibility")] string? Visibility);

    /// <summary>
    /// Rend un replay partageable, ou le retire du partage. C'est le SEUL geste qui autorise une
    /// autre borne à récupérer l'objet. Il modifie les métadonnées locales, jamais le manifeste
    /// immuable (CDC §66) : la visibilité est une décision de cette borne, pas une propriété du
    /// replay. `followers` est refusé tant que le transport contrôlé n'existe pas.
    /// </summary>
    [HttpPost("{id}/visibility")]
    public IActionResult SetVisibility(string id, [FromBody] VisibilityRequest? req)
    {
        if (!IsLocalCaller()) return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });
        if (_store.GetManifest(id) is null)
            return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });

        var v = req?.Visibility?.Trim().ToLowerInvariant();
        if (v is not ("public" or "private"))
            return BadRequest(new { ok = false, error = new { code = "VISIBILITY_INVALID" }, accepted = new[] { "public", "private" } });

        var meta = _store.GetMeta(id) ?? ReplayLocalMetadata.Fresh(id);
        _store.SaveMeta(meta with { Visibility = v });
        return Ok(new { ok = true, replay_id = id, visibility = v });
    }

    public sealed record PinRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("pinned")] bool Pinned);

    /// <summary>
    /// Épingle un replay : sa copie locale est conservée quoi qu'il arrive, aucun ménage ne peut
    /// l'effacer. C'est la politique de durabilité du CDC §46 appliquée à cette borne (un ancien
    /// #1, une finale) ; elle ne dit rien de ce que font les autres bornes.
    /// </summary>
    [HttpPost("{id}/pin")]
    public IActionResult SetPinned(string id, [FromBody] PinRequest? req)
    {
        if (!IsLocalCaller()) return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });
        if (_store.GetManifest(id) is null)
            return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });
        var meta = _store.GetMeta(id) ?? ReplayLocalMetadata.Fresh(id);
        _store.SaveMeta(meta with { Pinned = req?.Pinned ?? false });
        return Ok(new { ok = true, replay_id = id, pinned = req?.Pinned ?? false });
    }

    /// <summary>
    /// Publie ce replay sur le miroir de la plateforme : il devient public ET récupérable par
    /// n'importe quelle borne, y compris derrière un routeur domestique qui interdit toute
    /// connexion entrante. C'est un geste EXPLICITE, jamais déclenché par un enregistrement ou
    /// par le scellement d'un score.
    ///
    /// L'objet part avec son manifeste : sans lui, une borne qui n'a jamais vu ce replay ne
    /// saurait pas quel core ni quelle ROM employer.
    /// </summary>
    [HttpPost("{id}/publish")]
    public async Task<IActionResult> Publish(string id,
        [FromServices] ReplaySeedQueue queue, [FromServices] ReplaySeedService seeder, CancellationToken ct)
    {
        if (!IsLocalCaller()) return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });

        var manifest = _store.GetManifest(id);
        if (manifest is null) return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });

        // L'INTENTION D'ABORD. Elle est ecrite sur disque avant la moindre tentative reseau :
        // si la machine s'eteint pendant l'envoi, on perd la progression, jamais la decision.
        // C'est tout l'interet, sur une borne de particulier qu'on eteint juste apres la partie.
        queue.Enqueue(id, manifest.Object.Sha256);

        var meta = _store.GetMeta(id) ?? ReplayLocalMetadata.Fresh(id);
        _store.SaveMeta(meta with { Visibility = "public", PublicationState = "mirrored" });

        // Puis on tente tout de suite, sans faire dependre la reponse de la reussite : la file
        // reprendra si le reseau manque, si le transit refuse, ou si la machine s'arrete.
        try { await seeder.NudgeAsync(ct); } catch { /* la file reessaiera */ }

        var reste = queue.Read().Any(i => string.Equals(i.ReplayId, id, StringComparison.Ordinal));
        return Ok(new
        {
            ok = true,
            replay_id = id,
            visibility = "public",
            // Vrai = l'amorce le detient deja. Faux = c'est en file, et ca se fera tout seul.
            seeded = !reste,
        });
    }

    /// <summary>Retire ce replay du miroir et le repasse en privé.</summary>
    [HttpPost("{id}/unpublish")]
    public async Task<IActionResult> Unpublish(string id,
        [FromServices] ReplayTransitPublisher publisher, CancellationToken ct)
    {
        if (!IsLocalCaller()) return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });

        var result = await publisher.UnpublishAsync(id, ct);
        var meta = _store.GetMeta(id);
        if (meta is not null) _store.SaveMeta(meta with { Visibility = "private", PublicationState = "local" });
        // Le retrait local vaut même si le miroir n'a pas répondu : on ne laisse pas la borne
        // croire qu'elle partage encore alors qu'elle a décidé le contraire.
        return Ok(new { ok = true, replay_id = id, visibility = "private", mirror_cleared = result.Ok });
    }

    /// <summary>Reconstruit l'index depuis les manifests (maintenance).</summary>
    [HttpPost("rebuild-index")]
    public IActionResult RebuildIndex()
    {
        if (!IsLocalCaller()) return NotFound(new { ok = false, error = new { code = "REPLAY_NOT_FOUND" } });
        return Ok(new { rebuilt = _store.RebuildIndex().Count });
    }
}
