namespace RetroBat.Api.Speedrun;

/// <summary>
/// LES DEFIS OUVERTS SUR CETTE BORNE : ceux de l'index du site (§3.5 du CDC v1.4), tenus a jour par
/// <see cref="IndexDesDefisService"/>. La plateforme publie et retire, la borne ne decide pas. Une session en cours garde
/// son reglement : un defi retire ne coupe pas une tentative partie.
/// </summary>
public sealed class CatalogueDesDefis
{
    private volatile IReadOnlyDictionary<string, Reglement> _reglements = new Dictionary<string, Reglement>();

    public IReadOnlyCollection<Reglement> Tous => _reglements.Values.ToList();

    public Reglement? Trouver(string? defiId)
        => defiId is not null && _reglements.TryGetValue(defiId.Trim(), out var r) ? r : null;

    /// <summary>Remplace tout le catalogue d'un coup : un lecteur voit l'ancien ou le nouveau, jamais un melange.</summary>
    public void Remplacer(IEnumerable<Reglement> reglements)
    {
        var defis = new Dictionary<string, Reglement>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in reglements) defis[r.ChallengeId] = r;
        _reglements = defis;
    }
}
