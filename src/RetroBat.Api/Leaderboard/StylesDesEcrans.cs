namespace RetroBat.Api.Leaderboard;

/// <summary>La forme du portrait d'un personnage choisi, selon le type de jeu (CDC Ecrans v1.0, 1.1).</summary>
public enum FormeDuPortrait
{
    Eclat,
    Parallelogramme,
    Ecusson,
    Bulle,
    Blason,
    Hexagone,
    Losange,
}

/// <summary>L'habillage d'un type de jeu : police du titre et des pseudos, couleur par defaut, forme du portrait.</summary>
public sealed record StyleDEcran(string Type, string Police, string Couleur, FormeDuPortrait Forme);

/// <summary>
/// LA DECLINAISON PAR TYPE DE JEU (CDC Ecrans v1.0, 1.1) et les couleurs des places, qui ne changent jamais. Le type vient
/// du catalogue : le type de cote, sinon la famille.
/// </summary>
public static class StylesDesEcrans
{
    public const string Combat = "combat";
    public const string Course = "course";
    public const string Coop = "coop";
    public const string Puzzle = "puzzle";
    public const string Sport = "sport";
    public const string Strategie = "strategie";
    public const string Tir = "tir";

    /// <summary>Les chiffres et les libelles, sur tous les ecrans.</summary>
    public const string PoliceDesChiffres = "Barlow Condensed ExtraBold";
    public const string PoliceDesLibelles = "Barlow Condensed";

    private static readonly Dictionary<string, StyleDEcran> Styles = new(StringComparer.Ordinal)
    {
        [Combat] = new(Combat, "Bangers", "d4ff3a", FormeDuPortrait.Eclat),
        [Course] = new(Course, "Racing Sans One", "ffd23c", FormeDuPortrait.Parallelogramme),
        [Coop] = new(Coop, "Bungee", "ff4fa3", FormeDuPortrait.Ecusson),
        [Puzzle] = new(Puzzle, "Titan One", "ffffff", FormeDuPortrait.Bulle),
        [Sport] = new(Sport, "Graduate", "ff6a3d", FormeDuPortrait.Blason),
        [Strategie] = new(Strategie, "Cinzel Decorative", "e9c46a", FormeDuPortrait.Hexagone),
        [Tir] = new(Tir, "Russo One", "ff3b4f", FormeDuPortrait.Losange),
    };

    /// <summary>P1 violet, P2 orange, P3 cyan, P4 vert ; au-dela, un gris.</summary>
    public static readonly IReadOnlyList<string> CouleursDesPlaces = ["a259ff", "ff8a3d", "2fd5ff", "b6f03c"];

    public static string CouleurDeLaPlace(int? place)
        => place is >= 1 and <= 4 ? CouleursDesPlaces[place.Value - 1] : "8a93a6";

    public static string TypeDe(string? typeDeCote, string? famille) => typeDeCote switch
    {
        "combat" => Combat,
        "course" => Course,
        "puzzle" => Puzzle,
        "sport" => Sport,
        "strategie" => Strategie,
        "fps" or "arene" => Tir,
        _ => famille == "coop" ? Coop : Combat,
    };

    public static StyleDEcran Pour(string? type)
        => type is not null && Styles.TryGetValue(type, out var style) ? style : Styles[Combat];
}
