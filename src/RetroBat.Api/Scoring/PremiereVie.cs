using System.Text.RegularExpressions;

namespace RetroBat.Api.Scoring;

/// <summary>
/// LE 1LC, SCORE DE LA PREMIERE VIE (decision user du 2026-10-09). Des qu'une vie est perdue, le score
/// du moment devient le 1LC ; la meme partie continue de concourir pour le 1CC jusqu'au premier
/// credit, puis pour le plaisir. RIEN NE S'ARRETE A LA MORT : ni la partie, ni le replay, ni le 1CC.
/// Le 1LC se calcule apres la partie, sur les memes lectures ; seule la lecture du replay depuis un
/// classement 1LC s'arrete a la premiere mort.
///
/// La coupure se prend dans le run retenu pour le 1CC : la premiere perte de vie du joueur 1 a partir
/// de sa premiere lecture. Les lectures jusqu'a elle comprise restent (un mort ne marque plus) ; sans
/// perte dans le run, le 1LC est le run entier. Une fausse mort couperait le joueur trop tot : seuls
/// les COMPTEURS de vies comptent quand le .MEM en declare (voir <see cref="Compteurs"/>).
/// </summary>
public static class PremiereVie
{
    /// <summary>La raison de coupure portee par le passeport du 1LC.</summary>
    public const string Raison = "life_lost";

    /// <summary>
    /// LE JOUEUR A-T-IL MARQUE AVANT SA PREMIERE MORT ? Oui si le score a monte dans le run coupe, ou si
    /// ce run tient en une seule lecture positive (2026-10-09). Une partie de Master System se lance au
    /// bouton 1 ou 2, sans START : le 0 du depart n'est pas une lecture (le score n'a pas change), et
    /// 200 points marques avant la premiere mort donnaient un run d'une seule lecture, ou rien ne
    /// « montait » : le 1LC n'etait pas soumis. Un run ouvert par une remise a zero commence a 0.
    /// </summary>
    public static bool AMarque(IReadOnlyList<(long frame, long total)> run)
    {
        if (run.Count == 1) return run[0].total > 0;
        for (var i = 1; i < run.Count; i++)
        {
            if (run[i].total > run[i - 1].total) return true;
        }
        return false;
    }

    public static (List<(long frame, long total)> Run, long? Mort) Couper(
        IReadOnlyList<(long frame, long total)> run,
        IEnumerable<(long Frame, int Joueur)> pertes)
    {
        if (run.Count == 0) return (new List<(long frame, long total)>(), null);
        var debut = run[0].frame;
        long? mort = null;
        foreach (var perte in pertes.OrderBy(p => p.Frame))
        {
            // Le joueur 1 seul : une ligne sans joueur compte pour lui (le reporter y met 1).
            if (perte.Joueur > 1 || perte.Frame < debut) continue;
            mort = perte.Frame;
            break;
        }

        return mort is { } frame
            ? (run.Where(p => p.frame <= frame).ToList(), frame)
            : (run.ToList(), null);
    }

    /// <summary>
    /// UNE PERTE DE VIE QUE LE RUN RETENU N'A PAS VUE ET QU'ON NE SAIT PAS PLACER (25/11, 19XX de la borne : 1CC et 1LC
    /// publies au meme score, 104 600, apres trois vies perdues). Retourne la frame de la premiere perte du joueur 1
    /// SURVENUE AVANT le premier point du run retenu dans une partie deja en cours, sans rien qui montre qu'une partie
    /// neuve a commence entre elle et le run ; null si tout est clair.
    ///
    /// POURQUOI. <see cref="Couper"/> ecarte les pertes d'avant le run (la mort d'une partie precedente ne coupe pas
    /// celle-ci). Or le run retenu n'est pas toujours le debut de la partie : <c>SelectBestRun</c> coupe aux chutes de
    /// score, et un score qui retombe au milieu d'une partie (19XX affiche 183 puis 84 900 pendant l'ecran de fin de
    /// stage, 49 900 avant) ouvre un « nouveau run » qui contient le pic. Les pertes d'avant (frames 2541 et 5087) sont
    /// alors AVANT le run, ecartees, et le 1LC devenait « le run entier ». Les images ne sont pas en cause : pertes et
    /// lectures de score portent la meme image du coeur.
    ///
    /// LE CRITERE. Une perte est SANS PLACE quand (1) le joueur marquait deja avant elle (une lecture positive a la meme
    /// image ou avant : ce n'est pas l'initialisation des vies d'un depart), et (2) aucun credit consomme, START ni fin de
    /// partie vue ne tombe entre elle (exclue) et le premier point du run (inclus) : rien ne dit qu'une partie neuve a
    /// pris la suite. Dans ce cas on ne soumet pas de 1LC : mieux vaut ne rien classer qu'un 1LC faux.
    /// </summary>
    public static long? PerteSansPlace(
        IReadOnlyList<(long frame, long total)> run,
        IEnumerable<(long Frame, int Joueur)> pertes,
        IReadOnlyList<(long frame, long total)> trajectoire,
        IReadOnlyCollection<long> departsVus)
    {
        if (run.Count == 0) return null;
        var debut = run[0].frame;
        foreach (var perte in pertes.OrderBy(p => p.Frame))
        {
            if (perte.Joueur > 1 || perte.Frame >= debut) continue;
            var marquait = trajectoire.Any(p => p.frame <= perte.Frame && p.total > 0);
            if (!marquait) continue;
            var partieNeuve = departsVus.Any(d => d > perte.Frame && d <= debut);
            if (!partieNeuve) return perte.Frame;
        }

        return null;
    }

    /// <summary>
    /// LES COMPTEURS DE VIES DU .MEM : les adresses des lignes LOSE_LIFE a condition « decrease »
    /// (2026-10-09). C'est la regle du labo, un compteur de vies descend, appliquee a la borne. Un
    /// drapeau (eq, bit_true : sante a zero, image ou son de mort, bit d'affichage) peut parler sans
    /// mort, au demarrage ou en jeu ; un compteur ne baisse qu'a une mort. Les adresses sont
    /// normalisees comme celles des signaux (0x25 pour 0X0025).
    /// </summary>
    public static HashSet<string> Compteurs(string? mem)
    {
        var compteurs = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(mem)) return compteurs;
        foreach (var brute in mem.Split('\n'))
        {
            var ligne = brute.Split("--", 2)[0];
            if (!PerteDeVie.IsMatch(ligne) || !Descente.IsMatch(ligne) || LigneMorte.IsMatch(ligne)) continue;
            var adresse = Adresse.Match(ligne);
            if (adresse.Success) compteurs.Add(Normaliser(adresse.Groups[1].Value));
        }

        return compteurs;
    }

    /// <summary>
    /// Les pertes qui peuvent couper le 1LC : celles des compteurs quand l'un d'eux a parle, toutes
    /// sinon. Sans compteur entendu (adresse lue autrement par le moteur, mort sur la derniere vie qui
    /// ne decremente pas), on retombe sur toutes les pertes : jamais un 1LC sans coupure.
    /// </summary>
    public static List<(long Frame, int Joueur)> SurLesCompteurs(
        IReadOnlyList<(string Adresse, long Frame, int Joueur)> pertes, IReadOnlySet<string> compteurs)
    {
        var desCompteurs = pertes.Where(p => compteurs.Contains(Normaliser(p.Adresse))).ToList();
        return (desCompteurs.Count > 0 ? desCompteurs : pertes.ToList()).Select(p => (p.Frame, p.Joueur)).ToList();
    }

    /// <summary>L'adresse sous sa forme canonique : 0x suivi des chiffres hexadecimaux, sans zero de tete.</summary>
    public static string Normaliser(string adresse)
    {
        var t = adresse.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        t = t.TrimStart('0');
        return "0x" + (t.Length == 0 ? "0" : t.ToUpperInvariant());
    }

    private static readonly Regex PerteDeVie = new(@"action\s*=\s*[""']LOSE_LIFE[""']", RegexOptions.IgnoreCase);
    private static readonly Regex Descente = new(@"condition\s*=\s*[""']decrease[""']", RegexOptions.IgnoreCase);
    private static readonly Regex LigneMorte = new(@"no_(log|survey)\s*=\s*true", RegexOptions.IgnoreCase);
    private static readonly Regex Adresse = new(@"\baddress\s*=\s*(0[xX][0-9A-Fa-f]+)");
}
