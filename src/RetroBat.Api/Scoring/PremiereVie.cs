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
