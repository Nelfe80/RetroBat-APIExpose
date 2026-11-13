using System.Text.Json;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LE SCORE PROGRESSE (13/11, DoDonPachi DaiOuJou de Gogerad : 11 822 230 certifie et publie premier, sans jouer). Le jeu a ete
/// lance puis quitte en 15,5 s (931 images) ; aucun controle ne regardait COMMENT le score etait venu. Un score de jeu se
/// gagne : il monte par une suite de lectures, a un rythme que le jeu permet. Une valeur lue avant que le jeu n'ecrive son
/// score (memoire non initialisee, table des records, valeur BCD de passage) arrive d'un coup, sans montee, et peut depasser
/// tout ce que le jeu a jamais donne.
///
/// LA REGLE, la meme sur la borne et sur le site (Progression.php), jugee sur ce que le passeport signe : les checkpoints du
/// run (le sous-echantillonnage regulier de <see cref="IndicesSignes"/>), le plus grand pas signe (<c>metric.max_step</c>), la
/// duree de session (<c>timing.frame_count</c>). Un saut est IMPOSSIBLE quand, a la fois :
/// <list type="number">
/// <item>il apporte au moins <see cref="PartDominante"/> du score (le score n'a pas ete gagne par une montee) ;</item>
/// <item>il rapporte, a lui seul, plus que le RECORD publie du jeu (toutes regles du jeu) ;</item>
/// <item>et, si le jeu a un gain appris, il va plus de <see cref="MargeGain"/> fois plus vite que le GAIN MAXIMAL EN UNE
/// SECONDE appris des vraies parties publiees (le depart du run, une premiere lecture deja haute, n'a pas de duree : seules
/// les deux premieres conditions comptent).</item>
/// </list>
/// Jeu sans record ni gain appris (jamais publie) : le saut seul, qui fait <see cref="SautSeulPart"/> du score, dans un run
/// d'au plus <see cref="SautSeulHausses"/> hausses, dans la premiere minute de la session.
///
/// Pourquoi ces trois conditions ensemble (rejoue sur les 716 passeports de la borne et sur les classements publics,
/// E:\claude_travail\temp\certification) : de vraies parties ont de gros sauts (1942 : +10 000 d'un coup sur 13 020 ; Alex
/// Kidd : +4 255 sur 4 400 ; Bubble Bobble : +2 000 sur 2 010 ; Giga Wing : +9 645 000), souvent l'essentiel d'un 1LC court,
/// et le gain appris d'un jeu vient de joueurs moyens. Aucune n'a rapporte d'un seul saut plus que le record du jeu. Les
/// lectures parasites, si : Ms. Pac-Man 906 030 et 15 777 936 (record 47 050), Shinobi 10 000 000 (record 224 670),
/// Metal Slug 3 1 677 723 700, DaiOuJou 11 822 230 (record 1 999 820).
///
/// LE VERDICT EST LA QUARANTAINE, PAS LE REFUS : la borne ne s'arrete pas sur un saut impossible, elle le dit au journal et
/// envoie ; le site met la partie en quarantaine (<c>score.implausible_jump</c>), hors classement, jamais liberee par la
/// reconnaissance d'un emulateur, acceptee ou refusee a la main apres lecture du replay. Une partie exceptionnelle n'est
/// jamais perdue (les deux Altered Beast d'Ash, refusees a tort le 12/11, ne se rattrapaient pas) ; un faux score n'est
/// jamais publie.
///
/// LES VALEURS PARASITES DU DEMARRAGE (<see cref="SansParasiteDeDemarrage"/>) : sur la borne seulement, avant le choix du
/// meilleur run, le premier segment de la session (avant la premiere chute du score) est ecarte s'il commence deja haut sans
/// montee ou s'il est un saut impossible, et qu'une remise a zero le suit (le jeu a ecrit son score de depart). La vraie
/// partie, derriere, est alors celle qui concourt.
/// </summary>
public static class ProgressionDuScore
{
    /// <summary>Un saut qui apporte au moins la moitie du score.</summary>
    public const double PartDominante = 0.5;

    /// <summary>Plus de quatre fois le gain maximal en une seconde appris des vraies parties, en autant de temps.</summary>
    public const double MargeGain = 4;

    /// <summary>Sans aucune reference : un saut qui fait 90 % du score...</summary>
    public const double SautSeulPart = 0.9;

    /// <summary>... dans un run d'au plus deux hausses...</summary>
    public const int SautSeulHausses = 2;

    /// <summary>... dans la premiere minute de la session.</summary>
    public const double SautSeulAvantSecondes = 60;

    /// <summary>Les images d'une seconde, comme <see cref="DepartReel.ImagesParSeconde"/>.</summary>
    public const double ImagesParSeconde = DepartReel.ImagesParSeconde;

    /// <summary>Le code du verdict du site.</summary>
    public const string Code = "score.implausible_jump";

    /// <summary>Ce que le site sait du jeu : le gain maximal en une seconde appris, le record publie (null : inconnu).</summary>
    public readonly record struct Reperes(long? GainEnUneSeconde, long? Record)
    {
        public static Reperes Aucun => new(null, null);
    }

    /// <summary>
    /// Les indices des lectures du run que le passeport signe en checkpoints : un sous-echantillonnage regulier (au plus 97
    /// points), dont le dernier porte le score final. Le meme calcul que BuildPassport : la borne juge ce que le site verra.
    /// </summary>
    public static List<int> IndicesSignes(int n)
    {
        var indices = new List<int>();
        var pas = n > 96 ? (n / 96) + 1 : 1;
        for (var i = 0; i < n; i += pas) indices.Add(i);
        return indices;
    }

    /// <summary>Les checkpoints signes du run : (image, score), le dernier portant le score final.</summary>
    public static List<(long frame, long total)> PointsSignes(IReadOnlyList<(long frame, long total)> run, long scoreFinal)
    {
        var points = IndicesSignes(run.Count).Select(i => run[i]).ToList();
        if (points.Count > 0) points[^1] = (points[^1].frame, scoreFinal);
        return points;
    }

    /// <summary>Les hausses du score : chaque point plus haut que le precedent, et un depart deja au-dessus de zero.</summary>
    public static int Hausses(IReadOnlyList<(long frame, long total)> points)
    {
        if (points.Count == 0) return 0;
        var n = points[0].total > 0 ? 1 : 0;
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].total > points[i - 1].total) n++;
        }
        return n;
    }

    /// <summary>Un gain du run et sa duree : un ecart entre deux points, le depart du run depuis zero, le plus grand pas.</summary>
    public readonly record struct Fenetre(long Gain, double Secondes, long? Image, string Quoi)
    {
        public bool EstLeDepart => Quoi == DepartDuRun;
    }

    public const string DepartDuRun = "depart du run";
    public const string Ecart = "ecart";
    public const string PasSigne = "pas signe";

    /// <summary>Les gains a juger. Une duree inconnue (images non datees) vaut une seconde : un ecart sans date est un instant.</summary>
    public static List<Fenetre> Fenetres(IReadOnlyList<(long frame, long total)> points, long plusGrandPas, long imagesDeSession)
    {
        var sortie = new List<Fenetre>();
        if (points.Count > 0 && points[0].total > 0)
        {
            var f0 = points[0].frame > 0 ? points[0].frame : imagesDeSession;
            sortie.Add(new Fenetre(points[0].total, Math.Max(1.0, f0 / ImagesParSeconde), points[0].frame, DepartDuRun));
        }
        for (var i = 1; i < points.Count; i++)
        {
            var gain = points[i].total - points[i - 1].total;
            if (gain <= 0) continue;
            var images = points[i].frame - points[i - 1].frame;
            sortie.Add(new Fenetre(gain, images > 0 ? Math.Max(1.0, images / ImagesParSeconde) : 1.0, points[i].frame, Ecart));
        }
        if (plusGrandPas > 0) sortie.Add(new Fenetre(plusGrandPas, 1.0, null, PasSigne));
        return sortie;
    }

    /// <summary>La raison du saut impossible, ou null si le score a pu etre gagne ainsi.</summary>
    public static string? Juger(IReadOnlyList<(long frame, long total)> points, long score, long plusGrandPas, long imagesDeSession, Reperes reperes)
    {
        if (score <= 0 || points.Count == 0) return null;
        var gain = reperes.GainEnUneSeconde is > 0 ? reperes.GainEnUneSeconde : null;
        var record = reperes.Record is > 0 ? reperes.Record : null;
        foreach (var f in Fenetres(points, plusGrandPas, imagesDeSession))
        {
            if (f.Gain < PartDominante * score) continue;
            if (record is { } r)
            {
                if (f.Gain <= r) continue;
                if (!f.EstLeDepart && gain is { } g && f.Gain <= MargeGain * g * f.Secondes) continue;
                return $"{f.Quoi} de {f.Gain} en {f.Secondes:0} s : plus que le record publie du jeu ({r})"
                    + (gain is { } g2 ? $" et plus de {MargeGain:0} x le gain maximal appris ({g2} en une seconde)" : "")
                    + ", sans montee";
            }
            if (gain is null)
            {
                var seconde = (f.Image is > 0 ? f.Image.Value : imagesDeSession) / ImagesParSeconde;
                var hausses = Hausses(points);
                if (f.Gain >= SautSeulPart * score && hausses <= SautSeulHausses && seconde < SautSeulAvantSecondes)
                {
                    return $"{f.Quoi} de {f.Gain} a {seconde:0} s de session, {hausses} hausse(s) du score, et aucune reference du jeu (ni record, ni gain appris)";
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Le gain en une seconde qu'un run prouve (apprentissage) : son plus grand pas signe, ou le plus grand ecart de deux
    /// checkpoints dates, par seconde.
    /// </summary>
    public static long GainEnUneSeconde(IReadOnlyList<(long frame, long total)> points, long plusGrandPas)
    {
        double g = Math.Max(0, plusGrandPas);
        for (var i = 1; i < points.Count; i++)
        {
            var images = points[i].frame - points[i - 1].frame;
            if (images <= 0 || points[i].frame <= 0) continue;
            g = Math.Max(g, (points[i].total - points[i - 1].total) / Math.Max(1.0, images / ImagesParSeconde));
        }
        return (long)g;
    }

    /// <summary>
    /// LES LECTURES D'AVANT L'INITIALISATION DU SCORE ne comptent pas. Le premier segment de la trajectoire (jusqu'a la premiere
    /// chute du score) est ecarte quand une chute le suit (le jeu a remis son score a zero : sa vraie partie commence apres) et
    /// qu'il n'a pas ete gagne : sa premiere lecture est deja au moins la moitie de son sommet (deja haute, sans montee), ou il
    /// est un saut impossible (<see cref="Juger"/>). Repete tant que cela tient. Rend la trajectoire et ce qui en a ete ecarte.
    /// </summary>
    public static (List<(long frame, long total)> Trajectoire, int Ecartees, string? Raison) SansParasiteDeDemarrage(
        List<(long frame, long total)> trajectoire, long imagesDeSession, Reperes reperes)
    {
        var reste = trajectoire;
        var ecartees = 0;
        string? raison = null;
        while (true)
        {
            var fin = 1;
            while (fin < reste.Count && reste[fin].total >= reste[fin - 1].total) fin++;
            if (fin >= reste.Count) break;   // pas de remise a zero apres lui : rien ne dit ou commence la partie
            var segment = reste.Take(fin).ToList();
            var sommet = segment[^1].total;
            if (sommet <= 0) break;
            var dejaHaut = segment[0].total > 0 && segment[0].total >= PartDominante * sommet;
            var invraisemblable = Juger(PointsSignes(segment, sommet), sommet, PlusGrandPas(segment), imagesDeSession, reperes);
            if (!dejaHaut && invraisemblable is null) break;
            raison ??= dejaHaut
                ? $"premiere lecture deja haute ({segment[0].total}, sommet {sommet}) avant la remise a zero du jeu"
                : invraisemblable;
            ecartees += fin;
            reste = reste.Skip(fin).ToList();
        }
        return (reste, ecartees, raison);
    }

    /// <summary>Le plus grand ecart entre deux lectures consecutives (le <c>metric.max_step</c> du passeport).</summary>
    public static long PlusGrandPas(IReadOnlyList<(long frame, long total)> run)
    {
        long max = 0;
        for (var i = 1; i < run.Count; i++)
        {
            var pas = run[i].total - run[i - 1].total;
            if (pas > max) max = pas;
        }
        return max;
    }

    /// <summary>
    /// Les reperes du jeu que le site joint a sa reponse de profil (<c>progression</c> : <c>record</c>, <c>gain_1s</c>, au niveau
    /// du jeu, toutes regles). Un site d'avant ne les donne pas : aucun repere.
    /// </summary>
    public static Reperes DuCorpsDuProfil(string? corps)
    {
        if (string.IsNullOrWhiteSpace(corps)) return Reperes.Aucun;
        try
        {
            using var doc = JsonDocument.Parse(corps);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("progression", out var p) || p.ValueKind != JsonValueKind.Object)
            {
                return Reperes.Aucun;
            }
            return new Reperes(Entier(p, "gain_1s"), Entier(p, "record"));
        }
        catch (JsonException)
        {
            return Reperes.Aucun;
        }
    }

    private static long? Entier(JsonElement objet, string nom)
        => objet.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n > 0 ? n : null;
}
