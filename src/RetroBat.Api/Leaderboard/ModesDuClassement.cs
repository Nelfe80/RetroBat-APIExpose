using System.Text.Json.Serialization;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// LE CLASSEMENT D'UN JEU QUAND LE PLUGIN NE DIT PAS LEQUEL (API-1, 14/11). <c>GET /api/v1/nelfeplay/records/leaderboard</c>
/// sans <c>ruleset</c> repondait toujours pour « 1cc » : le marquee affichait le 1CC d'une partie jouee en 1LC ou dans un
/// mode propre au jeu (TGM2, Bubble Bobble), et rien pour un jeu dont la regle principale n'est pas « 1cc » (TGM2 ouvre sur
/// « 1cc-normal »). La regle se choisit maintenant ainsi, de la plus sure a la moins sure :
/// <list type="number">
/// <item>celle que l'appelant demande (<see cref="SourceDemandee"/>), inchange ;</item>
/// <item>celle du mode joue, lu dans la memoire du jeu en cours (<see cref="SourceJouee"/>) ;</item>
/// <item>la regle principale du jeu dans l'index des jeux ouverts, celle des onglets du panneau (<see cref="SourcePrincipale"/>) ;</item>
/// <item>« 1cc », comme avant, quand la borne ne connait pas les regles du jeu (<see cref="SourceParDefaut"/>).</item>
/// </list>
/// Pur : il se teste sans site ni partie.
/// </summary>
public static class ModesDuClassement
{
    public const string SourceDemandee = "request";
    public const string SourceJouee = "played";
    public const string SourcePrincipale = "principal";
    public const string SourceParDefaut = "default";
    public const string RegleParDefaut = "1cc";

    /// <summary>Un classement ouvert du jeu, tel qu'un plugin l'affiche.</summary>
    public sealed record ModeDuJeu(
        [property: JsonPropertyName("ruleset")] string Ruleset,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("family")] string Family,
        [property: JsonPropertyName("mode")] string Mode,
        [property: JsonPropertyName("principal")] bool Principal,
        [property: JsonPropertyName("played")] bool Played,
        [property: JsonPropertyName("selected")] bool Selected);

    /// <summary>La regle a demander au site, et d'ou elle vient.</summary>
    public static (string Regle, string Source) Choisir(string? demandee, string? jouee, IReadOnlyList<string> ouvertes)
    {
        if (!string.IsNullOrWhiteSpace(demandee)) return (demandee.Trim(), SourceDemandee);
        if (!string.IsNullOrWhiteSpace(jouee)) return (jouee.Trim(), SourceJouee);
        var principale = LeaderboardClient.ReglePrincipale(ouvertes ?? Array.Empty<string>());
        return principale.Length > 0 ? (principale, SourcePrincipale) : (RegleParDefaut, SourceParDefaut);
    }

    /// <summary>
    /// Les classements ouverts du jeu, dans l'ordre de l'index (le mode de depart en tete), chacun avec son libelle court
    /// (« 1CC-PU », celui des pastilles du panneau) et son nom (« 1CC POWER UP », celui de l'annonce de fin). Une regle
    /// jouee (ou demandee, pour un jeu de l'index) que l'index ne connait pas est ajoutee en fin de liste : le plugin la
    /// voit quand meme. Vide quand le jeu n'est ni dans l'index ni joue.
    /// </summary>
    public static IReadOnlyList<ModeDuJeu> Liste(IReadOnlyList<string> ouvertes, string? retenue, string? jouee)
    {
        var regles = (ouvertes ?? Array.Empty<string>())
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var principale = LeaderboardClient.ReglePrincipale(regles);
        // La regle retenue ne s'ajoute qu'a un jeu connu de l'index : le « 1cc » par defaut d'un jeu inconnu n'est pas un mode.
        var extras = regles.Count > 0 ? new[] { jouee, retenue } : new[] { jouee };
        foreach (var extra in extras)
        {
            if (!string.IsNullOrWhiteSpace(extra) && !regles.Contains(extra.Trim(), StringComparer.OrdinalIgnoreCase)) regles.Add(extra.Trim());
        }
        if (regles.Count == 0) return Array.Empty<ModeDuJeu>();
        return regles.Select(r =>
        {
            var (famille, mode) = LeaderboardInputService.FamilleEtMode(r);
            return new ModeDuJeu(
                r,
                LeaderboardInputService.LibelleDeRegle(r, regles),
                Scoring.ModesDeJeu.NomDuClassement(r),
                famille,
                mode,
                string.Equals(r, principale, StringComparison.OrdinalIgnoreCase),
                jouee is not null && string.Equals(r, jouee.Trim(), StringComparison.OrdinalIgnoreCase),
                retenue is not null && string.Equals(r, retenue.Trim(), StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }
}
