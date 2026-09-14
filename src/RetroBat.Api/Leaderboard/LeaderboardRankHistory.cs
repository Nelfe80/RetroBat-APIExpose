using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// Les rangs vus a la DERNIERE consultation d'un classement sur cette borne, pour dire qui est
/// monte ou descendu depuis. La plateforme ne garde pas l'historique des rangs : c'est la borne
/// qui s'en souvient, un petit fichier par jeu.
///
/// La reference reste FIXE pendant toute l'ouverture du panneau : le classement se relit pendant
/// qu'un replay s'envoie, et reecrire la reference a chaque relecture ferait disparaitre les
/// fleches aussitot apparues. On la remplace a l'ouverture suivante.
/// </summary>
public sealed class LeaderboardRankHistory
{
    private static string Racine => Path.Combine(RetroBatPaths.PluginRoot, "state", "leaderboard", "rangs");

    /// <summary>La cle d'une ligne : le joueur et son monde. Les anonymes n'ont pas de trajectoire.</summary>
    public static string Cle(LeaderboardClient.Ligne l)
        => l.Joueur.Length == 0 ? "" : l.Joueur.ToLowerInvariant() + "|" + l.Monde;

    /// <summary>Les rangs de la derniere consultation, vide si c'est la premiere.</summary>
    public IReadOnlyDictionary<string, int> Lire(string romGroup)
    {
        try
        {
            var fichier = Fichier(romGroup);
            if (fichier is null || !File.Exists(fichier)) return new Dictionary<string, int>();
            return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(fichier)) ?? new Dictionary<string, int>();
        }
        catch (Exception) { return new Dictionary<string, int>(); }
    }

    /// <summary>Retient les rangs vus maintenant, pour la prochaine consultation.</summary>
    public void Enregistrer(string romGroup, IEnumerable<LeaderboardClient.Ligne> lignes)
    {
        try
        {
            var fichier = Fichier(romGroup);
            if (fichier is null) return;
            var rangs = new Dictionary<string, int>();
            foreach (var l in lignes)
            {
                var cle = Cle(l);
                if (cle.Length > 0 && !rangs.ContainsKey(cle)) rangs[cle] = l.Rang;
            }
            if (rangs.Count == 0) return;   // un classement vide ou injoignable n'efface pas la memoire
            Directory.CreateDirectory(Racine);
            File.WriteAllText(fichier, JsonSerializer.Serialize(rangs));
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Le mouvement d'une ligne depuis la derniere consultation : positif = montee, negatif =
    /// descente, 0 = inchange ou inconnu (nouvel entrant, premiere consultation).
    /// </summary>
    public static int Mouvement(LeaderboardClient.Ligne l, IReadOnlyDictionary<string, int>? precedents)
    {
        if (precedents is null || precedents.Count == 0) return 0;
        var cle = Cle(l);
        return cle.Length > 0 && precedents.TryGetValue(cle, out var avant) ? avant - l.Rang : 0;
    }

    /// <summary>Un nom de fichier sur : un identifiant de jeu ne devient jamais un chemin.</summary>
    private static string? Fichier(string romGroup)
    {
        var nom = new string((romGroup ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return nom.Length == 0 ? null : Path.Combine(Racine, nom + ".json");
    }
}
