namespace RetroBat.Api.Scoring;

/// <summary>
/// LES OPTIONS DE TRICHE DU COEUR (24/11/2026, decision du user). NeoGeoff a joue Batsugun Special le 14/11 avec
/// <c>fbneo-cheat-3-batsugunsp-Always_have_Max_Weapon_PL1 = 1 - Always have Max Weapon PL1</c> : la borne a annonce la
/// partie certifiable, le site l'a mise en attente (settings.unknown) au lieu de la refuser. Toute option de triche du
/// coeur activee rend desormais la partie NON certifiable (<see cref="Motif"/>), au prevol comme a la fin.
/// Pur : la chaine est celle du wrapper, « cle=valeur;cle=valeur » (BuildCoreOptionsCanonical).
/// </summary>
public static class OptionsDeTriche
{
    /// <summary>Le motif du passeport, du prevol et du journal.</summary>
    public const string Motif = "game.cheat_enabled";

    /// <summary>
    /// Les noms lisibles des options de triche ACTIVES. Une cle est une option de triche quand elle contient « cheat »
    /// (FBNeo <c>fbneo-cheat-*</c>, MAME <c>mame_cheats_enable</c>, et tout coeur qui suit la meme convention).
    /// </summary>
    public static IReadOnlyList<string> Actives(string? options)
    {
        var noms = new List<string>();
        foreach (var paire in (options ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = paire.IndexOf('=');
            if (eq <= 0) continue;
            var cle = paire[..eq].Trim();
            if (!cle.Contains("cheat", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Active(paire[(eq + 1)..])) continue;
            var nom = Nom(cle);
            if (!noms.Contains(nom)) noms.Add(nom);
        }
        return noms;
    }

    /// <summary>
    /// Une valeur est ACTIVE sauf si elle est vide, « disabled », « off », « false », « no », « none », « 0 », « Normal ... »,
    /// ou une etiquette qui commence par 0 (« 0 - Disabled », ce que FBNeo liste : le premier choix est toujours l'arret).
    /// </summary>
    internal static bool Active(string? valeur)
    {
        var v = (valeur ?? "").Trim().Trim('"').ToLowerInvariant();
        if (v.Length == 0) return false;
        if (v is "0" or "off" or "false" or "no" or "none" or "disabled" or "disable" or "inactive") return false;
        if (v.StartsWith("disabled", StringComparison.Ordinal) || v.StartsWith("off ", StringComparison.Ordinal)) return false;
        // (25/11) Le DIP « Cheating » de Batsugun (fbneo-dipswitch-batsugun(sp)-Cheating) vaut « Normal game » d'usine :
        // ce n'est pas une triche (rapport d'Ash). « Normal ... » est l'arret ; l'autre valeur de ce DIP reste une triche.
        if (v == "normal" || v.StartsWith("normal ", StringComparison.Ordinal)) return false;
        // « 0 - Disabled », « 0: Off » : un zero seul en tete d'etiquette ; « 05 - ... » n'en est pas un.
        if (v[0] == '0' && (v.Length == 1 || !char.IsDigit(v[1]))) return false;
        return true;
    }

    /// <summary>
    /// « fbneo-cheat-3-batsugunsp-Always_have_Max_Weapon_PL1 » donne « Always have Max Weapon PL1 » ; « mame_cheats_enable »
    /// reste tel quel.
    /// </summary>
    internal static string Nom(string cle)
    {
        const string fbneo = "fbneo-cheat-";
        if (cle.StartsWith(fbneo, StringComparison.OrdinalIgnoreCase))
        {
            // fbneo-cheat-<n>-<jeu>-<Nom> : le nom suit le quatrieme tiret.
            var morceaux = cle.Split('-', 5);
            if (morceaux.Length == 5 && morceaux[4].Length > 0) return morceaux[4].Replace('_', ' ').Trim();
        }
        return cle;
    }
}
