using System.Globalization;
using System.Security.Cryptography;
using RetroBat.Api.Multiplayer;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Speedrun;

/// <summary>
/// LE DEPART SUR ETAT D'UN DEFI (demande du user du 01/11 : Green Hill 1 part pendant le panneau du niveau, Metal Slug 3 au
/// parachute, sans START ni choix du soldat). L'etat vit sous <c>resources/challenges/states</c>, livre par le Data Pack ;
/// le reglement en porte l'empreinte, verifiee avant chaque pose. La pose reprend le depart pret de MULTIPLAYER : le jeu
/// tourne, l'etat est ecrit dans le dossier des etats de RetroArch, puis charge par LOAD_STATE_SLOT. Le chargement vient
/// plus tard, quand la page de preparation s'efface : le chrono de Green Hill 1 part 2,7 s apres l'etat, celui de Metal
/// Slug 3 1,75 s apres, et la page reste pres de 3 s devant le jeu une fois qu'il tourne.
/// </summary>
public static class EtatsDesDefis
{
    /// <summary>Un emplacement a part : jamais celui des sauvegardes du joueur (0 a 9), ni celui du depart MULTIPLAYER (97).</summary>
    public const int Emplacement = 96;

    public static string Racine => Path.Combine(RetroBatPaths.PluginRoot, "resources", "challenges", "states");

    /// <summary>Le fichier de l'etat sous la racine ; null pour un chemin qui en sortirait.</summary>
    public static string? Chemin(EtatDeDepart etat, string? racine = null)
    {
        var relatif = etat.Fichier.Replace('\\', '/');
        if (relatif.Length == 0 || Path.IsPathRooted(relatif) || relatif.Split('/').Any(p => p is ".." or "." or "")) return null;
        return Path.Combine(racine ?? Racine, relatif.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Les octets de l'etat s'ils portent l'empreinte du reglement ; sinon la raison du refus.</summary>
    public static (byte[]? Octets, string? Refus) Lire(EtatDeDepart etat, string? racine = null)
    {
        if (Chemin(etat, racine) is not { } chemin) return (null, "state_path");
        byte[] octets;
        try { octets = File.ReadAllBytes(chemin); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, "state_missing"); }
        return string.Equals(Convert.ToHexString(SHA256.HashData(octets)), etat.Sha256, StringComparison.OrdinalIgnoreCase)
            ? (octets, null)
            : (null, "state_mismatch");
    }

    /// <summary>
    /// Pose l'etat, une fois que RetroArch joue le contenu, sans le charger. Rend null, ou la raison d'un echec. RetroArch nomme
    /// ses etats d'apres le contenu : <c>mslug3.state96</c> dans le dossier que donne <c>savestate_directory</c>.
    /// </summary>
    public static async Task<string?> PoserAsync(ICommandesRetroArch commandes, byte[] octets, string nomDuContenu, TimeSpan attente,
        CancellationToken ct)
    {
        string? dossier = null;
        var limite = DateTime.UtcNow + attente;
        while (DateTime.UtcNow < limite && dossier is null)
        {
            var statut = await commandes.DemanderAsync("GET_STATUS", ct).ConfigureAwait(false);
            if (statut is not null && (statut.Contains("PLAYING", StringComparison.Ordinal) || statut.Contains("PAUSED", StringComparison.Ordinal)))
            {
                var reponse = await commandes.DemanderAsync("GET_CONFIG_PARAM savestate_directory", ct).ConfigureAwait(false);
                const string prefixe = "GET_CONFIG_PARAM savestate_directory ";
                if (reponse is not null && reponse.StartsWith(prefixe, StringComparison.Ordinal) && reponse.Length > prefixe.Length)
                    dossier = reponse[prefixe.Length..].Trim();
            }

            if (dossier is null) await Task.Delay(500, ct).ConfigureAwait(false);
        }

        if (dossier is null) return "retroarch_unreachable";
        try
        {
            Directory.CreateDirectory(dossier);
            await File.WriteAllBytesAsync(Path.Combine(dossier, nomDuContenu + ".state" + Emplacement.ToString(CultureInfo.InvariantCulture)), octets, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "state_write";
        }

        return null;
    }

    /// <summary>Charge l'etat pose : RetroArch le prend a l'image suivante.</summary>
    public static Task ChargerAsync(ICommandesRetroArch commandes, CancellationToken ct)
        => commandes.EnvoyerAsync("LOAD_STATE_SLOT " + Emplacement.ToString(CultureInfo.InvariantCulture), ct);
}
