namespace RetroBat.Api.Adventure;

/// <summary>
/// LES REFUS DU SITE, EN TEXTES D'ADVENTURE (27/11) : chaque raison a son propre texte <c>adventure.refused.&lt;raison&gt;</c> au Data Pack.
/// Avant, tout refus d'un jeu d'Adventure passait par <c>multiplayer.refused.*</c> et le panneau Adventure disait « THIS GAME IS NOT OPEN
/// TO MULTIPLAYER YET » pour un simple plafond d'aventures. Pur.
/// </summary>
public static class AdventureRefus
{
    /// <summary>Les raisons connues et leur texte anglais de repli (le Data Pack porte les langues).</summary>
    internal static readonly IReadOnlyDictionary<string, string> Connus = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["offline"] = "NelfePlay is not answering",
        ["unauthorized"] = "Link this cabinet to your NelfePlay account",
        ["rate_limited"] = "Too many adventures: finish or delete one before starting another",
        ["quota"] = "Too many adventures: finish or delete one before starting another",
        ["bad_players"] = "An adventure needs at least two players",
        ["not_enough_players"] = "An adventure needs at least two players",
        ["unknown_game"] = "This game is not open to NelfePlay Adventure yet",
        ["not_multiplayer"] = "This game is not open to NelfePlay Adventure yet",
        ["adventure_unavailable"] = "NelfePlay Adventure is not available on this cabinet",
        ["content_refused"] = "This version of the game is not accepted",
        ["not_found"] = "This adventure no longer exists",
        ["not_member"] = "You are not a member of this adventure",
        ["exclusive"] = "This adventure is by invitation only",
        ["core_mismatch"] = "This adventure was made with another version of the game core",
        ["full"] = "This adventure has no free place",
        ["no_session"] = "No session is open for this adventure",
        ["session_open"] = "A session of this adventure is already open",
        ["object_missing"] = "The saved game is not on NelfePlay yet, try again",
        ["busy"] = "NelfePlay is busy, try again",
        ["unavailable"] = "NelfePlay is busy, try again",
        ["draft_exists"] = "An adventure is already waiting for its first game with friends",
        ["other"] = "Could not start the adventure",
    };

    /// <summary>La raison que le panneau sait dire : une raison inconnue devient <c>other</c>.</summary>
    public static string Raison(string? raison) => raison is { Length: > 0 } r && Connus.ContainsKey(r) ? r : "other";

    /// <summary>La cle de texte du Data Pack pour ce refus.</summary>
    public static string Cle(string? raison) => "adventure.refused." + Raison(raison);

    /// <summary>Le texte du refus : celui du Data Pack, sinon le texte anglais de repli. <paramref name="texte"/> rend (cle, defaut) -> texte.</summary>
    public static string Texte(string? raison, Func<string, string, string> texte) => texte(Cle(raison), Connus[Raison(raison)]);

    /// <summary>
    /// L'aventure doit-elle devenir active ? Seulement quand la partie part avec DEUX joueurs au moins (lancee seul : un brouillon reste un brouillon) et
    /// qu'elle ne l'est pas deja (<paramref name="etat"/> : l'etat connu du site, null = inconnu).
    /// </summary>
    public static bool DoitEtreActivee(int joueurs, string? etat) => joueurs >= 2 && !string.Equals(etat, "active", StringComparison.OrdinalIgnoreCase);
}
