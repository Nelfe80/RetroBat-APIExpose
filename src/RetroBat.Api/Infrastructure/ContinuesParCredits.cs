namespace RetroBat.Api.Infrastructure;

/// <summary>Une lecture du compteur de crédits (action CREDITS du .MEM), avec sa frame.</summary>
public readonly record struct EvenementDeCredit(int Value, long Frame);

/// <summary>Un appui sur START, avec le joueur qui l'a fait et la frame du moment.</summary>
public readonly record struct DepartDeJoueur(int Player, long Frame);

/// <summary>
/// LE CRÉDIT FAIT FOI (charte de la partie certifiée, décision du 2026-09-30).
///
/// Un continue, en arcade, c'est un crédit consommé. Les vies ne le disaient pas de façon fiable :
/// Double Dragon a été coupé à 29 960 sur une vie bonus, alors que l'écran montrait 41 520 au
/// premier crédit (« CREDIT 0 »). Le compteur de crédits, lui, ne bouge ni avec une vie bonus ni
/// avec la santé.
///
/// La règle : avant la partie, le joueur remet autant de crédits qu'il veut. Le premier crédit
/// consommé est le départ ; TOUT crédit consommé ensuite est un continue, sauf s'il accompagne
/// l'arrivée d'un joueur 2 ou plus. LA SESSION FAIT FOI : quitter le jeu termine la partie
/// (décision user du 2026-09-30), et une partie relancée sans quitter le jeu passe elle aussi par
/// un crédit, donc ne concourt pas. On garde le score d'avant.
///
/// Le score ne distingue plus rien : Double Dragon remet le score à zéro au continue (1 550, puis
/// 0 au continue, puis 1 500, au labo du 2026-09-30), ce qui le faisait passer pour une nouvelle
/// partie. Le premier crédit consommé est toujours le départ, même score non nul : une démo mal
/// reconnue laisse un score avant la partie, et y voir un continue ferait certifier la démo.
///
/// Sans ligne CREDITS au .MEM, rien ne coupe : le doute profite au joueur.
///
/// PARTIE OUVERTE AUX JOUEURS EN NETPLAY (décision user du 2026-09-30) : seul au départ, le
/// joueur joue pour le 1CC ; qu'un joueur le rejoigne, et la partie passe en 1CC MULTI, pour lui
/// comme pour les autres. La borne ne voit que les START de son propre panel : un crédit consommé
/// sans aucun START local vient d'un joueur distant. C'est une arrivée, pas un continue.
///
/// C'EST CUMULATIF (décision user du 2026-09-30) : le score fait seul avant l'arrivée reste un
/// 1CC, et il est bien qu'il soit certifié si le joueur ne l'avait jamais atteint ; la suite
/// bascule en 1CC MULTI. L'arrivée ferme donc le 1CC solo comme un continue le ferme, sans en
/// être un.
/// </summary>
public static class ContinuesParCredits
{
    /// <summary>L'écart toléré entre le START d'un joueur et le crédit qu'il consomme : 3 s.</summary>
    public const long FenetreArrivee = 180;

    public sealed record Bilan(IReadOnlyList<long> Coupes, bool PlusieursJoueurs)
    {
        /// <summary>Les crédits consommés par l'arrivée d'un joueur.</summary>
        public IReadOnlyList<long> Arrivees { get; init; } = [];

        /// <summary>Ce qui ferme le 1CC solo : un continue, ou l'arrivée d'un joueur.</summary>
        public IReadOnlyList<long> FinsDuSolo => Coupes.Concat(Arrivees).OrderBy(f => f).ToList();

        /// <summary>
        /// Le crédit du départ de CETTE borne, s'il a été vu. En netplay, la borne invitée rejoint
        /// une partie commencée : son joueur part au premier crédit consommé avec un START de son
        /// panel, et son score ne compte qu'à partir de là (1CC MULTI).
        /// </summary>
        public long? Depart { get; init; }
    }

    public static Bilan Calculer(
        IReadOnlyList<EvenementDeCredit> credits,
        IReadOnlyList<DepartDeJoueur> departs,
        bool ouverteAuxJoueurs = false)
    {
        var coupes = new List<long>();
        var arrivees = new List<long>();
        var plusieurs = false;
        var partieCommencee = false;
        long? depart = null;
        int? precedent = null;
        foreach (var credit in credits.OrderBy(c => c.Frame))
        {
            if (precedent is { } avant && credit.Value < avant)
            {
                switch (Nature(credit.Frame, partieCommencee, departs, ouverteAuxJoueurs))
                {
                    case Debit.ArriveeDUnJoueur:
                        plusieurs = true;
                        arrivees.Add(credit.Frame);
                        partieCommencee = true;   // depart a deux : le joueur 1 est parti aussi
                        break;
                    case Debit.ArriveeDistante:
                        plusieurs = true;         // le joueur de cette borne n'a peut-etre pas commence
                        arrivees.Add(credit.Frame);
                        break;
                    case Debit.Continue:
                        coupes.Add(credit.Frame);
                        break;
                    default:
                        partieCommencee = true;
                        depart ??= credit.Frame;
                        break;
                }
            }

            precedent = credit.Value;
        }

        return new Bilan(coupes, plusieurs) { Arrivees = arrivees, Depart = depart };
    }

    public enum Debit
    {
        /// <summary>Le départ de la partie.</summary>
        Depart,
        Continue,
        /// <summary>Un START de joueur 2 ou plus sur cette borne, au moment du crédit.</summary>
        ArriveeDUnJoueur,
        /// <summary>Partie ouverte en netplay, crédit consommé sans aucun START local.</summary>
        ArriveeDistante,
    }

    /// <summary>Ce que vaut un crédit consommé à cette frame.</summary>
    public static Debit Nature(
        long frame,
        bool partieCommencee,
        IReadOnlyList<DepartDeJoueur> departs,
        bool ouverteAuxJoueurs = false)
    {
        if (departs.Any(d => d.Player >= 2 && Math.Abs(d.Frame - frame) <= FenetreArrivee))
        {
            return Debit.ArriveeDUnJoueur;
        }

        if (ouverteAuxJoueurs && !departs.Any(d => Math.Abs(d.Frame - frame) <= FenetreArrivee))
        {
            return Debit.ArriveeDistante;
        }

        return partieCommencee ? Debit.Continue : Debit.Depart;
    }

    /// <summary>
    /// La trajectoire jusqu'au premier continue : rien de ce qui suit ne concourt, même quand le
    /// jeu remet le score à zéro. Le continue tombe entre deux lectures ; une lecture PILE sur sa
    /// frame appartient déjà au nouveau crédit (19xx ajoute son +1 de continue à la frame même).
    /// Des lectures sans frame (pont MAME d'avant mame-lua-0.3.2, tout à 0) ne laissent rien
    /// tomber entre deux lectures : rien n'est coupé, comme avant.
    /// </summary>
    public static List<(long frame, long total)> AvantLePremierContinue(
        IReadOnlyList<(long frame, long total)> trajectoire,
        IReadOnlyList<long> coupes)
    {
        if (coupes.Count == 0) return trajectoire.ToList();
        for (var i = 1; i < trajectoire.Count; i++)
        {
            var precedente = trajectoire[i - 1].frame;
            var courante = trajectoire[i].frame;
            if (coupes.Any(c => c > precedente && c <= courante))
            {
                return trajectoire.Take(i).ToList();
            }
        }

        return trajectoire.ToList();
    }
}

/// <summary>
/// LE COMPTEUR DE CONTINUES D'UN JEU CONSOLE (action CONTINUES du .MEM, decision user du
/// 2026-09-30). Sur console il n'y a pas de credit : le jeu compte lui-meme les continues qui
/// restent. Toute baisse de ce compteur pendant la session est un continue, comme un credit
/// consomme apres le depart en arcade. Sonic 1, verifie sous MAME : a l'ecran de continue, START
/// remet le score a zero, redonne trois vies et retire un continue au compteur.
///
/// Une nouvelle partie remet aussi le compteur a zero : refuser le continue puis relancer une
/// partie sans quitter le jeu se lit donc comme un continue. C'est la regle de l'arcade (la session
/// fait foi) : le bandeau dit de quitter puis relancer le jeu pour une partie certifiee.
/// </summary>
public static class ContinuesParCompteur
{
    /// <summary>Les frames ou le compteur de continues a baisse.</summary>
    public static IReadOnlyList<long> Coupes(IReadOnlyList<EvenementDeCredit> compteur)
    {
        var coupes = new List<long>();
        int? precedent = null;
        foreach (var e in compteur.OrderBy(c => c.Frame))
        {
            if (precedent is { } avant && e.Value < avant) coupes.Add(e.Frame);
            precedent = e.Value;
        }

        return coupes;
    }

    /// <summary>
    /// Le score d'avant le continue : la derniere lecture STRICTEMENT anterieure a sa frame. Sonic
    /// remet le score a zero a l'image meme du continue, et une lecture pile sur cette frame
    /// appartient deja a la suite.
    /// </summary>
    public static long ScoreAvant(IReadOnlyList<(long frame, long total)> lectures, long frame)
    {
        long score = 0;
        foreach (var (f, total) in lectures)
        {
            if (f >= frame) break;
            score = total;
        }

        return score;
    }
}

/// <summary>
/// CE QUI A FERME LE 1CC SOLO, garde dans le passeport signe sans etre affiche (decision user du
/// 2026-09-30) : la premiere des fins, avec sa raison et sa frame.
/// </summary>
public static class FinDuSolo
{
    public const string Continue = "continue";
    public const string JoueurRejoint = "player_joined";
    public const string CreditsIllisibles = "credits_unreadable";
    public const string ContinueConsole = "continue_counter";

    public static (string Raison, long Frame)? Premiere(params (string Raison, IEnumerable<long> Frames)[] sources)
    {
        (string Raison, long Frame)? premiere = null;
        foreach (var (raison, frames) in sources)
        {
            foreach (var frame in frames)
            {
                if (premiere is null || frame < premiere.Value.Frame) premiere = (raison, frame);
            }
        }

        return premiere;
    }
}
