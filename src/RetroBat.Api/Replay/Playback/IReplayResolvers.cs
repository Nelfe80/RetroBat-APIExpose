using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Storage;

namespace RetroBat.Api.Replay.Playback;

/// <summary>
/// Résout, sur CETTE machine, le core + la ROM d'un replay à partir de son manifeste.
/// Le <see cref="ReplayLaunchHint"/> local n'est qu'un accélérateur, jamais une condition.
/// </summary>
public interface IReplayRuntimeResolver
{
    /// <summary>Jamais null : quand rien n'est trouvé, le résultat dit quelle moitié manque et pourquoi.</summary>
    RuntimeResolution Resolve(ReplayManifest manifest, ReplayLaunchHint? hint);
}

/// <summary>
/// ⭐ LA seam NelfeNet. Question posée par le lecteur avant de lancer : « rends-moi l'objet de ce
/// replay disponible localement ». Le lecteur n'a PAS à savoir d'où il vient.
///
/// Implémentée par <c>NelfeNetSourceResolver</c> : l'objet est cherché ici d'abord, puis auprès
/// des pairs connus. Sans pair configuré, le comportement est celui d'avant NelfeNet, en local pur.
/// </summary>
public interface IReplaySourceResolver
{
    /// <summary>Vrai si l'objet est (devenu) disponible localement. L'implémentation peut le
    /// récupérer d'un pair, en vérifiant taille et hash avant de le ranger. L'intégrité est
    /// revérifiée juste après par le lecteur (R6), qui ne fait confiance à personne.</summary>
    Task<bool> EnsureObjectAvailableAsync(ReplayManifest manifest, CancellationToken ct);
}
