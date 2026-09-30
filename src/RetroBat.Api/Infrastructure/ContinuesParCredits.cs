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
/// </summary>
public static class ContinuesParCredits
{
    /// <summary>L'écart toléré entre le START d'un joueur et le crédit qu'il consomme : 3 s.</summary>
    public const long FenetreArrivee = 180;

    public sealed record Bilan(IReadOnlyList<long> Coupes, bool PlusieursJoueurs);

    public static Bilan Calculer(
        IReadOnlyList<EvenementDeCredit> credits,
        IReadOnlyList<DepartDeJoueur> departs)
    {
        var coupes = new List<long>();
        var plusieurs = false;
        var partieCommencee = false;
        int? precedent = null;
        foreach (var credit in credits.OrderBy(c => c.Frame))
        {
            if (precedent is { } avant && credit.Value < avant)
            {
                switch (Nature(credit.Frame, partieCommencee, departs))
                {
                    case Debit.ArriveeDUnJoueur:
                        plusieurs = true;
                        break;
                    case Debit.Continue:
                        coupes.Add(credit.Frame);
                        break;
                }

                partieCommencee = true;
            }

            precedent = credit.Value;
        }

        return new Bilan(coupes, plusieurs);
    }

    public enum Debit
    {
        /// <summary>Le départ de la partie.</summary>
        Depart,
        Continue,
        ArriveeDUnJoueur,
    }

    /// <summary>Ce que vaut un crédit consommé à cette frame.</summary>
    public static Debit Nature(
        long frame,
        bool partieCommencee,
        IReadOnlyList<DepartDeJoueur> departs)
    {
        if (departs.Any(d => d.Player >= 2 && Math.Abs(d.Frame - frame) <= FenetreArrivee))
        {
            return Debit.ArriveeDUnJoueur;
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
