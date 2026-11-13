using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LA DEMO N'EST JAMAIS UNE PARTIE (12/11, DoDonPachi DaiOuJou). La demonstration d'attract (bandeau DEMONSTRATION,
/// INSERT COIN, CREDITS:0) a ete certifiee et publiee au nom de la borne : 2 554 070, premiere des classements 1CC et
/// 1LC. Le START presse au panel pendant la demo, sans piece, ouvrait la fenetre de jeu (SortirDeDemo) : le jeu ne
/// demarrait pas, mais l'API se croyait en partie, ignorait des lors les DEMO_MODE du .MEM, et le meilleur run de la
/// demo partait au classement. Aucun credit n'avait ete consomme, le wrapper n'avait compte que 2 appuis (20 images
/// tenues) pour 41 s de score qui monte, et le replay n'en portait aucun.
///
/// Deux portes, sur ce que la session prouve, pas sur ce que la borne a cru en direct :
/// <list type="number">
/// <item>LE DEPART REEL : un jeu dont le .MEM sait lire le depart (une ligne CREDITS, ou GAME_START) doit l'avoir lu :
/// un credit consomme, le GAME_START, ou un depart impose (etat de depart du MULTIPLAYER, START du labo). Sans credit
/// consomme lu (piece et START pendant les 120 images de chauffe du wrapper, que la ligne ait parle ensuite ou non) : le
/// START du panel ne vaut depart que suivi des appuis d'une partie, assez d'appuis ET des touches tenues
/// (<see cref="StartSuiviDAppuis"/>), car des START tapes pendant l'attract font le nombre, jamais la duree tenue.
/// <para>UNE PIECE DE PLUS NE REND PAS UNE PARTIE MOINS VRAIE (12/11, Altered Beast, 217 500 et 219 100, 820 et 724
/// appuis, refuses en 1.9.172). Le depart tombe dans la chauffe (START 2,6 a 2,9 s apres le prevol), la ligne des credits
/// se tait ; puis une piece arrive en cours de partie, ou au SELECT du raccourci de sortie (SELECT = piece sous FBNeo),
/// et le jeu ne la consomme pas. La ligne avait parle sans baisser : refus sec, alors que la meme partie sans cette
/// piece passait par le START suivi des appuis. Une ligne qui ne fait que monter ne dit rien de plus qu'une ligne
/// muette : meme repli. La demo n'y gagne rien, puisqu'elle passait deja ce repli sans piece.</para></item>
/// <item>PRESQUE AUCUN APPUI : quel que soit le jeu, un run certifie demande au moins un appui toutes les
/// <see cref="SecondesParAppui"/> secondes de run, ou des touches tenues au moins <see cref="PartTenueMin"/> du run.
/// Une demo ne recoit du joueur que des START et des pieces tapes pendant l'attract (une dizaine d'images chacun) ; un
/// joueur, meme dans un jeu lent, tape souvent ou tient une direction, un tir. Mesure sur les 167 passeports publies de
/// la borne : la regle n'en refuse aucun autre que la demo (voir RAPPORT.md du 12/11).</item>
/// </list>
/// <para>LE RACCOURCI DE SORTIE N'EST PAS UNE PARTIE (13/11, DaiOuJou de Gogerad : 11 822 230 publie, jeu quitte en 15,5 s).
/// Sur 931 images, la porte 2 ne demandait que 2 appuis ; SELECT+START (le raccourci de sortie de RetroBat) en fait 2, et
/// SELECT met une piece sous FBNeo, que le START du raccourci consomme : porte 1 et porte 2 passaient. Desormais
/// (<see cref="RaccourciDeSortie"/>) : le START du raccourci n'est pas un depart, la piece qu'il consomme n'est pas un
/// credit de depart, et ses deux appuis ne comptent pas. Et un PLANCHER : jamais moins de <see cref="PlancherAppuis"/> appuis,
/// quelle que soit la duree (les touches tenues du raccourci suffisaient a un run de quelques images).</para>
/// <para>LA PARTIE COURTE PROUVE SA PROGRESSION : une session de moins de <see cref="PartieCourteSecondes"/> s doit montrer au
/// moins <see cref="HaussesMinPartieCourte"/> hausses du score dans son run (<see cref="PartieTropCourte"/>). La plus courte
/// des vraies parties publiees de la borne, Ms. Pac-Man 150 en 17 s, en a 15 ; un score venu d'un coup, une ou deux.</para>
/// Appuis inconnus (pont MAME, listener d'avant 0.336, champ absent) : la porte 2 ne dit rien, et la porte 1 demande
/// alors le depart lu par la memoire du jeu. La tentative de laboratoire (profil non ouvert, jamais classee) n'est pas
/// arretee : le refus y est seulement trace, la plateforme a besoin de la tentative pour ouvrir un profil.
/// </summary>
public static class DepartReel
{
    /// <summary>Un appui au moins toutes les dix secondes de run.</summary>
    public const double SecondesParAppui = 10;

    /// <summary>Ou des touches tenues au moins 5 % des images du run.</summary>
    public const double PartTenueMin = 0.05;

    /// <summary>Les images d'une seconde, a peu pres : a 50 Hz, le run parait plus court, la regle plus clemente.</summary>
    public const double ImagesParSeconde = 60;

    /// <summary>Jamais moins de trois appuis comptes, raccourci de sortie retire (il en fait deux a lui seul).</summary>
    public const long PlancherAppuis = 3;

    /// <summary>Une session de moins de 30 s...</summary>
    public const double PartieCourteSecondes = 30;

    /// <summary>... doit montrer au moins trois hausses du score dans son run.</summary>
    public const int HaussesMinPartieCourte = 3;

    /// <summary>Le SELECT (ou la touche hotkey) du raccourci precede son START d'une seconde et demie au plus.</summary>
    public const long RaccourciFenetreImages = 90;

    /// <summary>Et la session s'arrete dans les cinq secondes qui suivent ce START.</summary>
    public const long RaccourciFinImages = 300;

    /// <summary>Les appuis du raccourci : SELECT (ou hotkey) et START.</summary>
    public const long AppuisDuRaccourci = 2;

    /// <summary>Les appuis que le wrapper a comptes sur la session, et leurs images tenues ; null si non comptes.</summary>
    public readonly record struct Appuis(long? Nombre, long? ImagesTenues)
    {
        /// <summary>Les appuis sans ceux du raccourci de sortie, et sans ses images tenues (SELECT tenu jusqu'a la sortie).</summary>
        public Appuis SansLeRaccourci(Raccourci? raccourci, long imageDeFin)
        {
            if (raccourci is not { } r || Nombre is not { } n) return this;
            var tenues = Math.Max(1, imageDeFin - r.ImageSelect + 1) * AppuisDuRaccourci;
            return new Appuis(Math.Max(0, n - AppuisDuRaccourci), ImagesTenues is { } t ? Math.Max(0, t - tenues) : null);
        }
    }

    /// <summary>Le raccourci de sortie vu au panel : l'image de son SELECT (ou hotkey) et celle de son START.</summary>
    public readonly record struct Raccourci(long ImageSelect, long ImageStart);

    /// <summary>
    /// LE RACCOURCI DE SORTIE (SELECT+START de RetroBat, ou hotkey+START) : le dernier START du joueur 1, precede d'au plus
    /// <see cref="RaccourciFenetreImages"/> images par un SELECT ou une touche hotkey, et suivi de la fin de session dans les
    /// <see cref="RaccourciFinImages"/> images. Null : la session ne s'est pas terminee ainsi (ou le panel ne l'a pas lu).
    /// </summary>
    public static Raccourci? RaccourciDeSortie(IReadOnlyList<long> selects, IReadOnlyList<DepartDeJoueur> departs, long imageDeFin)
    {
        var starts = departs.Where(d => d.Player == 1).Select(d => d.Frame).ToList();
        if (starts.Count == 0 || selects.Count == 0) return null;
        var start = starts.Max();
        if (imageDeFin - start > RaccourciFinImages) return null;
        var avant = selects.Where(s => s <= start && start - s <= RaccourciFenetreImages).ToList();
        return avant.Count == 0 ? null : new Raccourci(avant.Max(), start);
    }

    /// <summary>Les START sans celui du raccourci : rien a partir de son SELECT.</summary>
    public static List<DepartDeJoueur> DepartsSansLeRaccourci(IReadOnlyList<DepartDeJoueur> departs, Raccourci? raccourci)
        => raccourci is { } r ? departs.Where(d => d.Frame < r.ImageSelect).ToList() : departs.ToList();

    /// <summary>Les lectures des credits sans la piece du raccourci ni sa consommation : rien a partir de son SELECT.</summary>
    public static List<EvenementDeCredit> CreditsSansLeRaccourci(IReadOnlyList<EvenementDeCredit> credits, Raccourci? raccourci)
        => raccourci is { } r ? credits.Where(c => c.Frame < r.ImageSelect).ToList() : credits.ToList();

    /// <summary>
    /// La partie trop courte pour prouver sa progression : moins de <see cref="PartieCourteSecondes"/> s de session (images du
    /// wrapper) et moins de <see cref="HaussesMinPartieCourte"/> hausses du score dans son run. Null : rien a redire, ou duree
    /// inconnue.
    /// </summary>
    public static string? PartieTropCourte(long imagesDeSession, int hausses)
        => imagesDeSession > 0 && imagesDeSession < PartieCourteSecondes * ImagesParSeconde && hausses < HaussesMinPartieCourte
            ? $"partie de {imagesDeSession / ImagesParSeconde:0} s et {hausses} hausse(s) du score : trop courte pour prouver sa progression "
              + $"(moins de {PartieCourteSecondes:0} s, il en faut {HaussesMinPartieCourte})"
            : null;

    /// <summary>Le proces-verbal du wrapper : press_count et press_frames_sum, absents avant le listener 0.336.</summary>
    public static Appuis DeLaSession(string? sessionJson)
    {
        if (string.IsNullOrWhiteSpace(sessionJson)) return new Appuis(null, null);
        try
        {
            var session = JsonNode.Parse(sessionJson) as JsonObject;
            if (session is null) return new Appuis(null, null);
            return new Appuis(Entier(session["press_count"]), Entier(session["press_frames_sum"]));
        }
        catch
        {
            return new Appuis(null, null);
        }
    }

    private static long? Entier(JsonNode? n)
    {
        try { return n is null ? null : n.GetValue<long>(); }
        catch { return null; }
    }

    /// <summary>
    /// Le joueur a-t-il appuye comme on joue, sur un run de <paramref name="imagesDuRun"/> images ? Null : on ne sait pas
    /// (appuis non comptes). Zero appui : jamais.
    /// </summary>
    public static bool? AppuisSuffisants(Appuis appuis, long imagesDuRun)
    {
        if (appuis.Nombre is not { } n) return null;
        if (n < PlancherAppuis) return false;
        var images = Math.Max(0, imagesDuRun);
        if (n >= AppuisRequis(images)) return true;
        return images > 0 && (appuis.ImagesTenues ?? 0) >= PartTenueMin * images;
    }

    /// <summary>Un appui par tranche de <see cref="SecondesParAppui"/> secondes commencee, un au moins.</summary>
    public static long AppuisRequis(long imagesDuRun)
        => Math.Max(1, (long)Math.Ceiling(Math.Max(0, imagesDuRun) / ImagesParSeconde / SecondesParAppui));

    /// <summary>La duree du run en images : de sa premiere a sa derniere lecture.</summary>
    public static long ImagesDuRun(IReadOnlyList<(long frame, long total)> run)
        => run.Count > 1 ? Math.Max(0, run[^1].frame - run[0].frame) : 0;

    /// <summary>
    /// Un credit consomme dans la session : une lecture plus basse que la precedente (le depart suppose de la chauffe
    /// compris), ou une premiere lecture qui tombe sur un START du joueur 1 (a <see cref="ContinuesParCredits.FenetreDepartInvisible"/>
    /// images pres) : la piece est passee pendant la chauffe du wrapper, et cette lecture est le credit que le START consomme.
    /// </summary>
    public static bool CreditConsomme(IReadOnlyList<EvenementDeCredit> credits, IReadOnlyList<DepartDeJoueur>? departs = null)
    {
        for (var i = 1; i < credits.Count; i++)
        {
            if (credits[i].Value < credits[i - 1].Value) return true;
        }
        if (credits.Count == 0 || departs is null) return false;
        var premiere = credits[0].Frame;
        return departs.Any(d => d.Player == 1 && Math.Abs(d.Frame - premiere) <= ContinuesParCredits.FenetreDepartInvisible);
    }

    /// <summary>Ce que le .MEM charge sait lire du depart : une ligne CREDITS vivante, une ligne GAME_START vivante.</summary>
    public static (bool Credits, bool GameStart) LignesDeDepart(string? mem)
    {
        if (string.IsNullOrEmpty(mem)) return (false, false);
        bool credits = false, gameStart = false;
        foreach (var brute in mem.Split('\n'))
        {
            var ligne = brute.Split("--", 2)[0];
            if (LigneMorte.IsMatch(ligne)) continue;
            if (LigneCredits.IsMatch(ligne)) credits = true;
            if (LigneGameStart.IsMatch(ligne)) gameStart = true;
        }
        return (credits, gameStart);
    }

    private static readonly Regex LigneCredits = new(@"action\s*=\s*[""']CREDITS[""']", RegexOptions.IgnoreCase);
    private static readonly Regex LigneGameStart = new(@"action\s*=\s*[""']GAME_START[""']", RegexOptions.IgnoreCase);
    private static readonly Regex LigneMorte = new(@"no_(log|survey)\s*=\s*true", RegexOptions.IgnoreCase);

    /// <summary>
    /// Le repli du depart non lu : le START, suivi des appuis d'une partie (assez d'appuis ET des touches tenues
    /// <see cref="PartTenueMin"/> du run). Plus strict que la porte 2 : des START tapes pendant l'attract, sans piece,
    /// font le nombre mais jamais la duree tenue.
    /// </summary>
    public static bool StartSuiviDAppuis(bool startVu, Appuis appuis, long imagesDuRun)
        => startVu && appuis.Nombre is { } n && n >= AppuisRequis(imagesDuRun)
            && imagesDuRun > 0 && (appuis.ImagesTenues ?? 0) >= PartTenueMin * imagesDuRun;

    /// <summary>Ce que la session a vu de son depart, et ce qu'elle sait des appuis.</summary>
    /// <param name="LigneDeCredits">Le .MEM charge declare une ligne CREDITS vivante.</param>
    /// <param name="LigneGameStart">Le .MEM charge declare une ligne GAME_START vivante.</param>
    /// <param name="Credits">Les lectures des credits de la session.</param>
    /// <param name="Departs">Les START lus au panel.</param>
    /// <param name="GameStartVu">Le GAME_START du .MEM est tombe.</param>
    /// <param name="DepartImpose">Un depart que la memoire du jeu ne dit pas : etat de depart du MULTIPLAYER, START du labo.</param>
    /// <param name="Appuis">Les appuis comptes par le wrapper.</param>
    /// <param name="ImagesDuRun">La duree du run certifie.</param>
    /// <param name="ImagesDeSession">La duree de la session (images du wrapper) ; 0 si inconnue.</param>
    /// <param name="Hausses">Les hausses du score dans les checkpoints signes du run.</param>
    public sealed record Bilan(
        bool LigneDeCredits,
        bool LigneGameStart,
        IReadOnlyList<EvenementDeCredit> Credits,
        IReadOnlyList<DepartDeJoueur> Departs,
        bool GameStartVu,
        bool DepartImpose,
        Appuis Appuis,
        long ImagesDuRun,
        long ImagesDeSession = 0,
        int Hausses = int.MaxValue)
    {
        public bool StartVu => Departs.Any(d => d.Player == 1);
    }

    /// <summary>La raison du refus, ou null si la session prouve un depart reel et des appuis de joueur.</summary>
    public static string? Refus(Bilan b)
    {
        var secondes = b.ImagesDuRun / ImagesParSeconde;
        if (AppuisSuffisants(b.Appuis, b.ImagesDuRun) == false)
        {
            return $"presque aucun appui du joueur : {b.Appuis.Nombre} appui(s), {b.Appuis.ImagesTenues ?? 0} image(s) tenue(s), "
                + $"pour {secondes:0} s de run (il en faut {PlancherAppuis} au moins, et {AppuisRequis(b.ImagesDuRun)} ou des touches tenues {PartTenueMin:P0} du run) : "
                + "la demo d'attract, ou un jeu laisse la";
        }
        if (PartieTropCourte(b.ImagesDeSession, b.Hausses) is { } courte) return courte;
        if (b.DepartImpose || b.GameStartVu) return null;
        if (b.LigneDeCredits)
        {
            if (CreditConsomme(b.Credits, b.Departs)) return null;
            // Des pieces lues, aucune consommee : le depart a pu tomber dans la chauffe, et la piece venir apres (en
            // partie, au raccourci de sortie). Comme une ligne muette : le START suivi des appuis d'une partie tranche.
        }
        else if (!b.LigneGameStart)
        {
            return null;   // le .MEM ne sait pas lire le depart : la porte 2 seule
        }
        if (StartSuiviDAppuis(b.StartVu, b.Appuis, b.ImagesDuRun)) return null;
        if (!b.LigneDeCredits)
            return "ni GAME_START, ni START suivi des appuis d'une partie : la partie n'a jamais commence (demo d'attract)";
        return b.Credits.Count > 0
            ? "aucun credit consomme alors que la ligne des credits est lue (piece jamais jouee), et pas de START suivi des appuis d'une partie : le START n'a lance aucune partie (demo d'attract)"
            : "aucun credit consomme, et pas de START suivi des appuis d'une partie : la partie n'a jamais commence (demo d'attract)";
    }
}
