using RetroBat.Api.Replay.Models;

namespace RetroBat.Api.Replay.Storage;

// ─────────────────────────────────────────────────────────────────────────────
// R7 — les SEAMS du Replay. On ne découpe pas par goût de l'abstraction : on
// découpe exactement ce que NelfeNet fera VARIER (d'où vient un manifeste, d'où
// vient un objet), pour que le lecteur n'ait jamais à savoir si le replay est né
// ici ou est arrivé d'une autre borne.
//
// Ce qui reste volontairement NON abstrait : les réactions (locales par nature,
// cf. CDC) et les chemins internes du store.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Le manifeste technique IMMUABLE d'un replay (nelfe.replay.v2).</summary>
public interface IReplayManifestStore
{
    string ManifestPath(string replayId);
    void SaveManifest(ReplayManifest manifest);
    ReplayManifest? GetManifest(string replayId);
    IReadOnlyList<ReplayManifest> ListManifests();
}

/// <summary>
/// L'objet .replay, adressé par son SHA-256. C'est LA brique que NelfeNet devra pouvoir
/// alimenter autrement (peer, relais, archive) — d'où l'interface.
/// </summary>
public interface IReplayObjectStore
{
    /// <summary>Emplacement local de l'objet pour ce hash (le fichier peut ne pas exister).</summary>
    string ObjectPath(string sha256);

    /// <summary>Zone de travail locale (fichiers temporaires de session).</summary>
    string TempRoot { get; }

    /// <summary>Range un fichier .replay dans le magasin adressé-par-contenu et renvoie sa référence.</summary>
    Task<ReplayObjectRef> ImportObjectAsync(string sourcePath, CancellationToken ct);

    /// <summary>R6 : l'objet présent correspond-il VRAIMENT au manifeste (taille + SHA-256) ?</summary>
    Task<bool> VerifyObjectAsync(ReplayObjectRef obj, CancellationToken ct);

    /// <summary>
    /// L'objet est-il détenu, brut OU compressé au repos ? C'est la question à poser pour savoir
    /// si un replay est disponible : <see cref="ObjectPath"/> n'existe plus forcément sur le disque.
    /// </summary>
    bool HasObject(string sha256);

    /// <summary>
    /// Le chemin du BRUT, matérialisé depuis la forme compressée si besoin, vérifié contre son
    /// empreinte. À appeler avant toute LECTURE du contenu (RetroArch, pair, relais, capture).
    /// null si l'objet n'est pas détenu ou si sa forme compressée ne redonne pas l'empreinte.
    /// </summary>
    Task<string?> EnsureRawAsync(string sha256, CancellationToken ct);

    /// <summary>Supprime l'objet sous ses deux formes.</summary>
    void DeleteObject(string sha256);
}

/// <summary>Métadonnées LOCALES et mutables (jamais dans le manifeste) : hint de lancement,
/// carte du player, score/rang estampillés…</summary>
public interface IReplayMetadataStore
{
    ReplayLocalMetadata? GetMeta(string replayId);
    void SaveMeta(ReplayLocalMetadata meta);
}

/// <summary>Vue DÉRIVÉE (reconstructible depuis les manifestes) — jamais une source de vérité.</summary>
public interface IReplayIndex
{
    string IndexPath { get; }
    IReadOnlyList<ReplayIndexEntry> RebuildIndex();
    IReadOnlyList<ReplayIndexEntry> ReadIndex();
}
