using System.Collections.Concurrent;
using System.Security.Cryptography;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Speedrun;

/// <summary>
/// DEUX JEUX D'ETATS SELON LE COEUR (decision du user, 22/11/2026). Un etat de sauvegarde ne se charge que dans le coeur qui
/// l'a fait : FBNeo 261102 a change la taille d'etat de 18 jeux, l'ancien coeur accepte un etat neuf puis plante au premier
/// retro_run, le nouveau refuse un ancien. Un reglement de defi (<c>states</c>) et un depart MULTIPLAYER
/// (<c>core_sha256</c>) portent donc plusieurs etats, chacun avec les empreintes SHA-256 des dll de coeur pour lesquelles
/// il vaut. Le coeur qui compte est la dll du coeur reel installe (celle que RetroArch chargera), lue AVANT le lancement.
/// Une entree qui nomme des coeurs est STRICTE : un coeur inconnu d'elle ne la prend jamais (aucun chargement d'une autre
/// taille). Une entree sans liste vaut pour tous les coeurs (ancien format).
/// </summary>
public static class EtatsParCoeur
{
    /// <summary>Longueur minimale d'une empreinte abregee (les journaux de l'attestation en montrent 16).</summary>
    public const int LongueurMinimale = 16;

    /// <summary>L'etat vaut-il pour ce coeur ? Liste absente ou vide : tout coeur. Sinon l'empreinte doit commencer par l'une des entrees.</summary>
    public static bool Correspond(IReadOnlyList<string>? coeurs, string? empreinteDuCoeur)
    {
        if (coeurs is not { Count: > 0 }) return true;
        if (string.IsNullOrWhiteSpace(empreinteDuCoeur)) return false;
        var empreinte = empreinteDuCoeur.Trim();
        return coeurs.Any(c => c.Trim() is { Length: >= LongueurMinimale } liste
            && empreinte.StartsWith(liste, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Une empreinte de coeur bien formee : hexadecimale, 16 a 64 caracteres.</summary>
    public static bool EstUneEmpreinte(string? texte)
        => texte is { } t && t.Trim().Length is >= LongueurMinimale and <= 64 && t.Trim().All(Uri.IsHexDigit);

    private static readonly ConcurrentDictionary<string, (long Taille, DateTime Ecrit, string Sha)> Memoire = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SHA-256 d'un fichier, memorise tant que sa taille et sa date ne changent pas ; null s'il ne se lit pas.</summary>
    public static string? EmpreinteDuFichier(string chemin)
    {
        try
        {
            var info = new FileInfo(chemin);
            if (!info.Exists) return null;
            if (Memoire.TryGetValue(chemin, out var vu) && vu.Taille == info.Length && vu.Ecrit == info.LastWriteTimeUtc) return vu.Sha;
            using var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var sha = Convert.ToHexString(SHA256.HashData(flux)).ToLowerInvariant();
            Memoire[chemin] = (info.Length, info.LastWriteTimeUtc, sha);
            return sha;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// L'empreinte du coeur REEL installe pour ce nom (« fbneo »), avant tout lancement. Montage <c>core_proxy</c> : le vrai coeur
    /// est dans <c>cores\</c> (RetroBat le met a jour). Ancien montage <c>cores_real</c> : <c>cores\</c> porte le wrapper et le
    /// vrai coeur est dans <c>cores_real\</c>. Le wrapper se reconnait a son empreinte (celle de <c>wrapper\wrapper.dll</c>).
    /// Choisie plutot que la derniere attestation : l'attestation n'existe qu'apres un lancement, et le lanceur d'ES met le
    /// coeur a jour AU lancement (22/11 : 13 min de telechargement) : seul le fichier dit ce que RetroArch chargera.
    /// </summary>
    public static string? EmpreinteDuCoeurInstalle(string? coeur, string? racineRetroArch = null, string? wrapper = null)
    {
        if (string.IsNullOrWhiteSpace(coeur)) return null;
        var retroarch = racineRetroArch ?? Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch");
        var nom = coeur.Trim() + "_libretro.dll";
        var dansCores = EmpreinteDuFichier(Path.Combine(retroarch, "cores", nom));
        var duWrapper = EmpreinteDuFichier(wrapper ?? Path.Combine(RetroBatPaths.PluginRoot, "wrapper", "wrapper.dll"));
        if (dansCores is not null && duWrapper is not null && string.Equals(dansCores, duWrapper, StringComparison.Ordinal))
            return EmpreinteDuFichier(Path.Combine(retroarch, "cores_real", nom));
        return dansCores;
    }
}
