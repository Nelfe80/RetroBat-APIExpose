using System.Reflection;

namespace RetroBat.Api.Speedrun;

/// <summary>
/// LES DEFIS OUVERTS SUR CETTE BORNE, le temps du prototype : leurs reglements sont embarques dans l'API
/// (Speedrun/Reglements). Au lot S4, le site les publie et les sert par un index des defis ouverts (§3.5 du
/// CDC v1.4) ; ce catalogue lira alors cet index au lieu des ressources.
/// </summary>
public static class CatalogueDesDefis
{
    private const string Prefixe = "RetroBat.Api.Speedrun.Reglements.";

    private static readonly Lazy<IReadOnlyDictionary<string, Reglement>> Reglements = new(Charger);

    public static IReadOnlyCollection<Reglement> Tous => Reglements.Value.Values.ToList();

    public static Reglement? Trouver(string? defiId)
        => defiId is not null && Reglements.Value.TryGetValue(defiId.Trim(), out var r) ? r : null;

    private static IReadOnlyDictionary<string, Reglement> Charger()
    {
        var assemblage = typeof(CatalogueDesDefis).Assembly;
        var defis = new Dictionary<string, Reglement>(StringComparer.OrdinalIgnoreCase);
        foreach (var nom in assemblage.GetManifestResourceNames().Where(n => n.StartsWith(Prefixe, StringComparison.Ordinal)))
        {
            using var flux = assemblage.GetManifestResourceStream(nom);
            if (flux is null) continue;
            using var lecteur = new StreamReader(flux);
            var reglement = Reglement.Lire(lecteur.ReadToEnd());
            defis[reglement.ChallengeId] = reglement;
        }

        return defis;
    }
}
