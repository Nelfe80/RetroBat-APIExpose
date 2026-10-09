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

    /// <summary>
    /// La valeur de chaque octet GAME_MODE, par adresse. Un jeu a un seul octet de mode (Tetris)
    /// n'en a pas besoin ; un jeu dont le mode tient a plusieurs drapeaux (Bubble Bobble) se relit
    /// avec les profils, qui disent les drapeaux de chaque mode (voir ModesParDrapeaux).
    /// </summary>
    public IReadOnlyDictionary<string, int> Drapeaux { get; init; } = new Dictionary<string, int>();

    public ContexteDeJeu AvecMode(int valeur) => Mode == valeur ? this : this with { Mode = valeur };

    public ContexteDeJeu AvecDrapeau(string adresse, int valeur)
    {
        var cle = ModesDeJeu.Adresse(adresse);
        if (Drapeaux.TryGetValue(cle, out var avant) && avant == valeur) return this;
        var copie = new Dictionary<string, int>(Drapeaux, StringComparer.Ordinal) { [cle] = valeur };
        return this with { Drapeaux = copie };
    }

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
        // Le 1LC et le speedrun non plus : chacun a son passeport, mesure dans la meme partie.
        profils = DuSolo(profils);
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

    /// <summary>
    /// UN MODE DIT PAR PLUSIEURS DRAPEAUX (2026-10-06). Bubble Bobble a trois codes d'ecran titre,
    /// chacun leve son propre octet : Original (0xE5D1), Power-Up (0xE5D2), Super (0xE5DB). La valeur
    /// lue d'un drapeau (1) ne dit pas lequel ; le profil d'un tel mode declare donc la valeur de
    /// chaque drapeau, `mode.flags` : {"0xE5D1": 0, "0xE5D2": 1, "0xE5DB": 0} pour Power-Up.
    /// </summary>
    public sealed record ModeParDrapeaux(int Valeur, IReadOnlyDictionary<string, int> Drapeaux);

    /// <summary>Le mode d'une combinaison de drapeaux qu'aucun profil ne couvre : pas de classement.</summary>
    public const int SansClassement = -1;

    /// <summary>Les modes que les profils definissent par des drapeaux. Vide pour un jeu a un seul octet.</summary>
    public static List<ModeParDrapeaux> ModesParDrapeaux(IEnumerable<JsonElement> profils)
    {
        var modes = new List<ModeParDrapeaux>();
        foreach (var profil in profils)
        {
            if (profil.ValueKind != JsonValueKind.Object
                || !profil.TryGetProperty("mode", out var m) || m.ValueKind != JsonValueKind.Object
                || !m.TryGetProperty("value", out var v) || !v.TryGetInt32(out var valeur)
                || !m.TryGetProperty("flags", out var f) || f.ValueKind != JsonValueKind.Object) continue;
            var drapeaux = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var d in f.EnumerateObject())
            {
                if (d.Value.TryGetInt32(out var x)) drapeaux[Adresse(d.Name)] = x;
            }
            if (drapeaux.Count > 0 && modes.All(e => e.Valeur != valeur)) modes.Add(new ModeParDrapeaux(valeur, drapeaux));
        }
        return modes;
    }

    /// <summary>
    /// Le mode que disent les drapeaux mesures : celui dont chaque drapeau a la valeur lue. Un drapeau
    /// jamais signale vaut 0, sa valeur au demarrage (le wrapper ne dit rien d'un octet qui ne bouge
    /// pas). Un drapeau leve que le mode ne connait pas l'exclut. Aucun mode : SansClassement.
    /// </summary>
    public static int ModeDesDrapeaux(IReadOnlyList<ModeParDrapeaux> modes, IReadOnlyDictionary<string, int> mesures)
    {
        foreach (var mode in modes)
        {
            var conforme = mode.Drapeaux.All(d => (mesures.TryGetValue(d.Key, out var v) ? v : 0) == d.Value)
                && mesures.All(m => mode.Drapeaux.ContainsKey(m.Key) || m.Value == 0);
            if (conforme) return mode.Valeur;
        }
        return SansClassement;
    }

    /// <summary>Le contexte relu avec les modes par drapeaux ; inchange sans eux, ou sans drapeau mesure.</summary>
    public static ContexteDeJeu Resoudre(ContexteDeJeu contexte, IReadOnlyList<ModeParDrapeaux> modes)
        => modes.Count == 0 || contexte.Drapeaux.Count == 0
            ? contexte
            : contexte with { Mode = ModeDesDrapeaux(modes, contexte.Drapeaux) };

    /// <summary>Les profils des parties seules : tous sauf la categorie 1CC MULTI et ses modes.</summary>
    public static List<JsonElement> SansLeMulti(IReadOnlyList<JsonElement> profils)
        => profils.Where(p => !EstMulti(Regle(p))).ToList();

    /// <summary>
    /// Les profils du 1CC d'une partie seule : ni le 1CC MULTI, ni le 1LC, ni le speedrun. Un profil
    /// 1LC ouvert ne doit jamais recevoir le 1CC d'une partie (2026-10-09).
    /// </summary>
    public static List<JsonElement> DuSolo(IReadOnlyList<JsonElement> profils)
        => profils.Where(p => !EstMulti(Regle(p)) && !Est1LC(Regle(p)) && !EstSpeedrun(Regle(p))).ToList();

    /// <summary>
    /// LE 1LC (decision user du 2026-10-09) : le score de la premiere vie. La partie le mesure en meme
    /// temps que son 1CC ; 1lc, et 1lc-&lt;mode&gt; pour un jeu a modes.
    /// </summary>
    public static bool Est1LC(string? regle)
        => regle is { } r && (r == "1lc" || r.StartsWith("1lc-", StringComparison.Ordinal));

    /// <summary>Le speedrun, et ses variantes : jamais le classement du 1CC d'une partie.</summary>
    public static bool EstSpeedrun(string? regle)
        => regle is { } r && (r == "speedrun" || r.StartsWith("speedrun-", StringComparison.Ordinal));

    /// <summary>
    /// Le profil 1LC d'une partie seule. Un seul, sans mode : celui-la. Des 1LC a modes : celui du
    /// mode joue ; sans signal, celui du mode de demarrage. Null : le jeu n'a pas de 1LC ouvert.
    /// </summary>
    public static JsonElement? ChoisirProfil1LC(IReadOnlyList<JsonElement> profils, int? modeJoue)
    {
        var uneVie = profils.Where(p => Est1LC(Regle(p))).ToList();
        if (uneVie.Count == 0) return null;
        var aModes = uneVie.Where(p => ModeDuProfil(p) is not null).ToList();
        if (aModes.Count == 0) return uneVie[0];
        var mode = modeJoue ?? (ChoisirProfil(profils, null) is { } solo ? ModeDuProfil(solo) : null);
        foreach (var p in aModes)
        {
            if (ModeDuProfil(p) == mode) return p;
        }
        return null;
    }

    /// <summary>
    /// LA CATEGORIE 1CC MULTI ET SES MODES (2026-10-06) : 1cc-multi, et 1cc-multi-super,
    /// 1cc-multi-power-up... quand un jeu a des modes qui se cumulent avec le multi (Bubble Bobble).
    /// </summary>
    public static bool EstMulti(string? regle)
        => regle is { } r && (r == "1cc-multi" || r.StartsWith("1cc-multi-", StringComparison.Ordinal));

    private static string? Regle(JsonElement profil)
        => profil.ValueKind == JsonValueKind.Object
           && profil.TryGetProperty("ruleset", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : null;

    /// <summary>
    /// Le profil 1CC MULTI d'une partie a plusieurs. Un seul, sans mode : celui-la, quel que soit le
    /// mode (comme avant). Des multis a modes : celui du mode joue ; sans signal, celui du mode de
    /// demarrage du jeu. Null quand le mode joue n'a pas de 1CC MULTI : rien a soumettre.
    /// </summary>
    public static JsonElement? ChoisirProfilMulti(IReadOnlyList<JsonElement> profils, int? modeJoue)
    {
        var multis = profils.Where(p => EstMulti(Regle(p))).ToList();
        if (multis.Count == 0) return null;
        var aModes = multis.Where(p => ModeDuProfil(p) is not null).ToList();
        if (aModes.Count == 0) return multis[0];
        var mode = modeJoue ?? (ChoisirProfil(profils, null) is { } solo ? ModeDuProfil(solo) : null);
        foreach (var p in aModes)
        {
            if (ModeDuProfil(p) == mode) return p;
        }
        return null;
    }

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
