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
/// un credit consomme, le GAME_START, ou un depart impose (etat de depart du MULTIPLAYER, START du labo). Une ligne de
/// credits qui a parle sans que rien ne la consomme (une piece mise, jamais jouee) : refus. Une ligne qui n'a jamais
/// parle (piece et START pendant les 120 images de chauffe du wrapper) : le START du panel ne vaut depart que suivi des
/// appuis d'une partie, assez d'appuis ET des touches tenues (<see cref="StartSuiviDAppuis"/>), car des START tapes
/// pendant l'attract font le nombre, jamais la duree tenue.</item>
/// <item>PRESQUE AUCUN APPUI : quel que soit le jeu, un run certifie demande au moins un appui toutes les
/// <see cref="SecondesParAppui"/> secondes de run, ou des touches tenues au moins <see cref="PartTenueMin"/> du run.
/// Une demo ne recoit du joueur que des START et des pieces tapes pendant l'attract (une dizaine d'images chacun) ; un
/// joueur, meme dans un jeu lent, tape souvent ou tient une direction, un tir. Mesure sur les 167 passeports publies de
/// la borne : la regle n'en refuse aucun autre que la demo (voir RAPPORT.md du 12/11).</item>
/// </list>
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

    /// <summary>Les appuis que le wrapper a comptes sur la session, et leurs images tenues ; null si non comptes.</summary>
    public readonly record struct Appuis(long? Nombre, long? ImagesTenues);

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
        if (n <= 0) return false;
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
    public sealed record Bilan(
        bool LigneDeCredits,
        bool LigneGameStart,
        IReadOnlyList<EvenementDeCredit> Credits,
        IReadOnlyList<DepartDeJoueur> Departs,
        bool GameStartVu,
        bool DepartImpose,
        Appuis Appuis,
        long ImagesDuRun)
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
                + $"pour {secondes:0} s de run (il en faut {AppuisRequis(b.ImagesDuRun)}, ou des touches tenues {PartTenueMin:P0} du run) : "
                + "la demo d'attract, ou un jeu laisse la";
        }
        if (b.DepartImpose || b.GameStartVu) return null;
        if (b.LigneDeCredits)
        {
            if (CreditConsomme(b.Credits, b.Departs)) return null;
            // La ligne des credits a parle (une piece, une lecture) et rien ne l'a consommee : aucun START n'a lance de partie.
            if (b.Credits.Count > 0)
                return "aucun credit consomme alors que la ligne des credits est lue : le START n'a lance aucune partie (demo d'attract)";
        }
        else if (!b.LigneGameStart)
        {
            return null;   // le .MEM ne sait pas lire le depart : la porte 2 seule
        }
        if (StartSuiviDAppuis(b.StartVu, b.Appuis, b.ImagesDuRun)) return null;
        return b.LigneDeCredits
            ? "aucun credit consomme, et pas de START suivi des appuis d'une partie : la partie n'a jamais commence (demo d'attract)"
            : "ni GAME_START, ni START suivi des appuis d'une partie : la partie n'a jamais commence (demo d'attract)";
    }
}
