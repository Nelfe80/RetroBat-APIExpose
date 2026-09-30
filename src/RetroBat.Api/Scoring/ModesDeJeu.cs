using System.Text.Json;
using System.Text.Json.Nodes;

namespace RetroBat.Api.Scoring;

/// <summary>
/// Le MODE et la DIFFICULTE d'une partie, tels que la memoire du jeu les dit (2026-09-29).
///
/// Un meme jeu peut se jouer de plusieurs facons qui ne se comparent pas : Tetris sur Game Boy a
/// un type A (score sans fin) et un type B (25 lignes a faire). Chaque mode a son classement, donc
/// son profil (un ruleset par mode). Le .MEM porte une ligne GAME_MODE ; le profil dit quelle
/// valeur il couvre, et le reporter soumet au profil du mode joue.
///
/// La difficulte choisie au depart (niveau, hauteur) est « libre et affichee » par defaut : elle
/// ne coupe pas le classement, elle s'affiche a cote du score. Un profil peut aussi la restreindre
/// (liste de valeurs acceptees), c'est alors la plateforme qui refuse.
///
/// LE SIGNAL PEUT MANQUER. Le wrapper ne dit rien pendant ses deux secondes de prechauffe, et une
/// ligne « change » ne parle que quand la valeur bouge : un joueur qui garde le mode et le niveau
/// de demarrage ne produit aucun signal. Le profil declare donc la valeur de demarrage du jeu
/// (mode par defaut, valeur initiale de chaque champ), et c'est elle qui vaut sans signal.
/// </summary>
public sealed record ContexteDeJeu(int? Mode, IReadOnlyDictionary<string, int> Difficulte)
{
    public static readonly ContexteDeJeu Vide = new(null, new Dictionary<string, int>());

    public ContexteDeJeu AvecMode(int valeur) => Mode == valeur ? this : this with { Mode = valeur };

    public ContexteDeJeu AvecDifficulte(string adresse, int valeur)
    {
        var cle = ModesDeJeu.Adresse(adresse);
        if (Difficulte.TryGetValue(cle, out var avant) && avant == valeur) return this;
        var copie = new Dictionary<string, int>(Difficulte, StringComparer.Ordinal) { [cle] = valeur };
        return this with { Difficulte = copie };
    }
}

/// <summary>
/// Les lectures d'un seul mode : celles prises pendant qu'il etait en vigueur. Une lecture sans
/// mode mesure appartient au mode de demarrage (<paramref name="ParDefaut"/>).
/// </summary>
public sealed record FiltreDeMode(int? Mode, int? ParDefaut)
{
    public bool Garde(ContexteDeJeu contexte) => (contexte.Mode ?? ParDefaut) == Mode;
}

public static class ModesDeJeu
{
    /// <summary>
    /// Les modes joues dans une session, dans l'ordre ou ils apparaissent. Une lecture sans mode
    /// mesure compte pour le mode de demarrage : le 0 lu au lancement ne fait pas un mode a part.
    /// </summary>
    public static List<int?> ModesJoues(IReadOnlyList<ContexteDeJeu> contextes, int? parDefaut)
        => contextes.Select(c => c.Mode ?? parDefaut).Distinct().ToList();

    public const string SignalMode = "GAME_MODE";
    public const string SignalDifficulte = "GAME_DIFFICULTY";

    /// <summary>Le pont MAME ecrit 0xffc2, le wrapper 0XFFC2 ou 0X00FFC2 : une seule cle.</summary>
    public static string Adresse(string adresse)
    {
        var t = (adresse ?? "").Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        t = t.TrimStart('0');
        return "0x" + (t.Length == 0 ? "0" : t.ToUpperInvariant());
    }

    /// <summary>La valeur que couvre le profil, ou null pour un jeu sans modes.</summary>
    public static int? ModeDuProfil(JsonElement profil)
        => profil.ValueKind == JsonValueKind.Object
           && profil.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.Object
           && m.TryGetProperty("value", out var v) && v.TryGetInt32(out var n)
            ? n
            : null;

    private static bool ModeParDefaut(JsonElement profil)
        => profil.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.Object
           && m.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Le profil de la partie parmi ceux ouverts pour le jeu. Sans modes : le premier (il n'y en a
    /// qu'un). Avec modes : celui dont la valeur est le mode joue, ou celui du demarrage quand aucun
    /// signal n'est venu. Null quand le mode joue n'a pas de classement : rien a soumettre.
    /// </summary>
    public static JsonElement? ChoisirProfil(IReadOnlyList<JsonElement> profils, int? modeJoue)
    {
        // Le 1CC MULTI n'est jamais le classement d'une partie seule : il a sa propre soumission
        // (NelfePlayScoringReporter.SoumettreMultiAsync). La plateforme le met deja en dernier.
        profils = SansLeMulti(profils);
        if (profils.Count == 0) return null;
        var aModes = profils.Where(p => ModeDuProfil(p) is not null).ToList();
        if (aModes.Count == 0) return profils[0];
        if (modeJoue is { } joue)
        {
            foreach (var p in aModes)
            {
                if (ModeDuProfil(p) == joue) return p;
            }
            return null;
        }
        foreach (var p in aModes)
        {
            if (ModeParDefaut(p)) return p;
        }
        return null;
    }

    /// <summary>Les profils des parties seules : tous sauf la categorie 1CC MULTI.</summary>
    public static List<JsonElement> SansLeMulti(IReadOnlyList<JsonElement> profils)
        => profils.Where(p => !(p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("ruleset", out var r) && r.ValueKind == JsonValueKind.String
            && r.GetString() == "1cc-multi")).ToList();

    /// <summary>
    /// Le mode d'une partie : celui que le profil couvre (la plateforme verifie qu'il a ete joue).
    /// Sans modes au profil, le mode mesure s'il y en a un : une partie de labo le montre.
    /// </summary>
    public static int? ModePourLePasseport(JsonElement profil, ContexteDeJeu contexte)
        => ModeDuProfil(profil) is { } couvert
            ? (contexte.Mode ?? couvert)
            : contexte.Mode;

    /// <summary>
    /// La difficulte a signer : chaque champ que le profil declare, avec la valeur mesuree ou, a
    /// defaut, la valeur de demarrage du jeu. Sans champ au profil, ce qui a ete mesure (labo).
    /// Null quand il n'y a rien a dire.
    /// </summary>
    public static JsonObject? DifficultePourLePasseport(JsonElement profil, ContexteDeJeu contexte)
    {
        var sortie = new JsonObject();
        if (profil.ValueKind == JsonValueKind.Object
            && profil.TryGetProperty("difficulty", out var d) && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty("fields", out var champs) && champs.ValueKind == JsonValueKind.Array)
        {
            foreach (var champ in champs.EnumerateArray())
            {
                if (champ.ValueKind != JsonValueKind.Object
                    || !champ.TryGetProperty("address", out var a) || a.ValueKind != JsonValueKind.String) continue;
                var cle = Adresse(a.GetString() ?? "");
                if (contexte.Difficulte.TryGetValue(cle, out var mesure)) sortie[cle] = mesure;
                else if (champ.TryGetProperty("initial", out var i) && i.TryGetInt32(out var initiale)) sortie[cle] = initiale;
            }
        }
        else
        {
            foreach (var (cle, valeur) in contexte.Difficulte.OrderBy(p => p.Key, StringComparer.Ordinal)) sortie[cle] = valeur;
        }
        return sortie.Count > 0 ? sortie : null;
    }

    /// <summary>
    /// Le contexte en vigueur au DEBUT du run retenu. On retrouve le pic du run dans les lectures,
    /// puis on remonte tant que le score ne retombe pas : la ou il retombe, une autre partie
    /// commencait. Le debut compte, et pas le pic : un jeu peut reutiliser l'octet du niveau choisi
    /// pour le niveau en cours. Sans pic retrouve, le contexte courant.
    ///
    /// LE DEBUT, C'EST LA PREMIERE MONTEE DU SCORE, PAS SA PREMIERE LECTURE. Une lecture ne
    /// s'inscrit que quand le score CHANGE : le 0 d'une premiere partie est celui lu au demarrage
    /// du jeu, AVANT le menu ou le joueur choisit son mode et son niveau. Mesure du 2026-09-29 sur
    /// Tetris : le 0 a 20:38:56, le niveau choisi a 20:39:05, les premiers points a 20:39:15.
    /// </summary>
    public static ContexteDeJeu ContexteDuRun(
        IReadOnlyList<(long frame, long total)> lectures,
        IReadOnlyList<ContexteDeJeu> contextes,
        IReadOnlyList<(long frame, long total)> run,
        ContexteDeJeu courant)
    {
        if (run.Count == 0 || lectures.Count == 0 || contextes.Count != lectures.Count) return courant;
        var pic = run[^1];
        var i = -1;
        for (var k = lectures.Count - 1; k >= 0; k--)
        {
            if (lectures[k] == pic) { i = k; break; }
        }
        if (i < 0) return courant;
        var sommet = i;
        while (i > 0 && lectures[i - 1].total <= lectures[i].total) i--;
        return contextes[i < sommet ? i + 1 : i];
    }
}
