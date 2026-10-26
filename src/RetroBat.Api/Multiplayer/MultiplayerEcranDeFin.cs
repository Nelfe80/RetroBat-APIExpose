using System.Runtime.Versioning;
using RetroBat.Api.Leaderboard;

namespace RetroBat.Api.Multiplayer;

/// <summary>
/// CE QUE L'ECRAN DE FIN MONTRE DES STATISTIQUES (CDC Ecrans v1.0, 2.2 et 2.3), calcule hors de tout dessin :
/// <list type="bullet">
/// <item>chaque mesure du profil, par place : valeur, jauge relative au meilleur, etoile sur la meilleure valeur (seulement si
/// les valeurs different : une etoile partagee par tous ne dit rien) ;</item>
/// <item>les distinctions : un joueur autre que le vainqueur, seul en tete d'une categorie, en recoit une (une au plus,
/// la maitrise d'abord, puis l'efficacite, puis l'execution) ; son nom est celui de la mesure, sinon celui de la categorie ;</item>
/// <item>la jauge globale : la part de chaque place sur la mesure d'efficacite (les degats d'un duel).</item>
/// </list>
/// Une valeur non mesuree reste nulle : jamais un zero invente.
/// </summary>
[SupportedOSPlatform("windows")]
public static class MultiplayerEcranDeFin
{
    public sealed record ValeurDeFin(MultiplayerStatsMesure Mesure, long? Valeur, string Etat);

    public sealed record Calcul(
        IReadOnlyDictionary<int, IReadOnlyList<LeaderboardOverlayService.MesureDeFin>> Mesures,
        IReadOnlyDictionary<int, string> Distinctions,
        LeaderboardOverlayService.JaugeDeFin? Jauge);

    private static readonly string[] OrdreDesDistinctions = ["maitrise", "efficacite", "execution"];

    public static Calcul Calculer(MultiplayerStatsProfil profil, IReadOnlyDictionary<int, IReadOnlyList<ValeurDeFin>> valeurs, int? gagnante,
        Func<string, string> T)
    {
        var places = valeurs.Keys.Order().ToList();
        var mesures = places.ToDictionary(p => p, _ => new List<LeaderboardOverlayService.MesureDeFin>());
        var meneurs = new Dictionary<string, (int Place, MultiplayerStatsMesure Mesure)>(StringComparer.Ordinal);
        LeaderboardOverlayService.JaugeDeFin? jauge = null;
        foreach (var mesure in profil.Metrics)
        {
            var lues = places.Select(p =>
            {
                var v = valeurs[p].FirstOrDefault(x => x.Mesure.Id == mesure.Id);
                return (Place: p, Valeur: v is { Etat: MultiplayerStatsValeur.Mesuree or MultiplayerStatsValeur.Zero } ? v.Valeur : null);
            }).ToList();
            var connues = lues.Where(x => x.Valeur is not null).Select(x => x.Valeur!.Value).ToList();
            var plusEstMieux = mesure.Better != "lower";
            long? meilleure = connues.Count == 0 ? null : plusEstMieux ? connues.Max() : connues.Min();
            var departage = connues.Distinct().Count() > 1;
            var max = connues.Count == 0 ? 0 : connues.Max();
            var min = connues.Count == 0 ? 0 : connues.Min();
            var libelle = T(mesure.Label).ToUpperInvariant();
            var icone = IconesPixel.PourLaMesure(mesure.Id, mesure.Category);
            foreach (var (place, valeur) in lues)
            {
                double part = valeur is null ? 0
                    : plusEstMieux ? (max > 0 ? valeur.Value / (double)max : 0)
                    : max == min ? 1 : 0.3 + 0.7 * (max - valeur.Value) / (max - min);
                mesures[place].Add(new(icone, libelle, valeur, Math.Clamp(part, 0, 1), departage && valeur == meilleure));
            }

            if (departage && meilleure is not null && lues.Count(x => x.Valeur == meilleure) == 1)
            {
                meneurs[mesure.Category] = (lues.First(x => x.Valeur == meilleure).Place, mesure);
            }

            if (jauge is null && mesure.Category == "efficacite" && plusEstMieux && connues.Count >= 2 && connues.Sum() > 0)
            {
                var total = (double)connues.Sum();
                jauge = new(Nom(T, "multiplayer.end.share." + mesure.Id, mesure.Label).ToUpperInvariant(),
                    lues.Where(x => x.Valeur is not null).Select(x => new LeaderboardOverlayService.PartDeLaJauge(x.Place, x.Valeur!.Value / total)).ToList());
            }
        }

        var distinctions = new Dictionary<int, string>();
        foreach (var categorie in OrdreDesDistinctions)
        {
            if (meneurs.TryGetValue(categorie, out var meneur) && meneur.Place != gagnante && !distinctions.ContainsKey(meneur.Place))
            {
                distinctions[meneur.Place] = Nom(T, "multiplayer.badge." + meneur.Mesure.Id, "multiplayer.badge.category." + categorie);
            }
        }

        return new(mesures.ToDictionary(m => m.Key, m => (IReadOnlyList<LeaderboardOverlayService.MesureDeFin>)m.Value), distinctions, jauge);
    }

    /// <summary>Le texte de la premiere cle connue (une cle absente revient telle quelle), ou rien.</summary>
    private static string Nom(Func<string, string> T, params string[] cles)
    {
        foreach (var cle in cles)
        {
            var texte = T(cle);
            if (texte.Length > 0 && texte != cle)
            {
                return texte;
            }
        }

        return string.Empty;
    }
}
