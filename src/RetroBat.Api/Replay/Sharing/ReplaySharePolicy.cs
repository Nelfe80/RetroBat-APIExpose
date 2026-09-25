using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Sharing;

/// <summary>Verdict de partage d'un objet, avec sa raison (journalisable, jamais renvoyée telle
/// quelle au demandeur : on ne lui apprend pas ce qui existe).</summary>
public sealed record ShareDecision(bool Allowed, string? ReplayId, string Reason);

/// <summary>
/// NelfeNet, première garde : une borne ne sert un objet QUE si trois conditions sont réunies
/// (CDC §51). Elle ne sert jamais un chemin local arbitraire, et l'identifiant demandé est un
/// hash, donc rien de ce que l'appelant écrit ne devient un chemin.
///
/// 1. le partage est activé sur cette borne (opt-in explicite, défaut FERMÉ) ;
/// 2. l'objet est ENREGISTRÉ : un manifeste connu le référence. Un fichier qui traînerait dans
///    le magasin sans manifeste n'est pas servable ;
/// 3. la visibilité du replay le permet. Un replay `private` n'est ni servi ni annoncé (§48),
///    et c'est le défaut de tout replay.
///
/// La visibilité vit dans les métadonnées LOCALES, jamais dans le manifeste (§66) : rendre un
/// replay public ne réécrit pas un document immuable.
/// </summary>
public sealed class ReplaySharePolicy
{
    private readonly IReplayIndex _index;
    private readonly IReplayMetadataStore _meta;
    private readonly IReplayObjectStore _objects;
    private readonly IConfiguration _config;
    private readonly ILogger<ReplaySharePolicy> _logger;

    public ReplaySharePolicy(IReplayIndex index, IReplayMetadataStore meta, IReplayObjectStore objects,
        IConfiguration config, ILogger<ReplaySharePolicy> logger)
    {
        _index = index; _meta = meta; _objects = objects; _config = config; _logger = logger;
    }

    /// <summary>Cette borne accepte-t-elle de servir ses objets publics ? Défaut : non.</summary>
    public bool SharingEnabled => _config.GetValue("Replay:Share:Enabled", false);

    /// <summary>
    /// Le réseau local est-il considéré comme de confiance pour la SEULE surface de partage ?
    /// Même doctrine que la clé d'API historique de la borne (« vide = LAN de confiance ») :
    /// chez un particulier avec deux bornes, il n'y a personne pour recopier une clé d'une machine
    /// à l'autre, et la découverte LAN ne servirait à rien. Ça n'ouvre jamais l'administration,
    /// jamais les replays privés, et jamais au-delà d'une adresse privée. Défaut : non.
    /// </summary>
    public bool TrustLocalNetwork => _config.GetValue("Replay:Share:TrustLocalNetwork", false);

    public ShareDecision Evaluate(string sha256)
    {
        if (!SharingEnabled) return new ShareDecision(false, null, "sharing_disabled");

        var entries = _index.ReadIndex();
        if (entries.Count == 0) entries = _index.RebuildIndex();

        var entry = entries.FirstOrDefault(e => string.Equals(e.ObjectSha256, sha256, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return new ShareDecision(false, null, "object_not_registered");

        var visibility = _meta.GetMeta(entry.ReplayId)?.Visibility ?? "private";
        if (!string.Equals(visibility, "public", StringComparison.OrdinalIgnoreCase))
            return new ShareDecision(false, entry.ReplayId, "visibility_" + visibility.ToLowerInvariant());

        if (!_objects.HasObject(sha256))
            return new ShareDecision(false, entry.ReplayId, "object_missing");

        _logger.LogDebug("Replay share : objet {Sha} servable ({ReplayId}).", Short(sha256), entry.ReplayId);
        return new ShareDecision(true, entry.ReplayId, "ok");
    }

    private static string Short(string sha) => sha.Length <= 8 ? sha : sha[..8];
}
