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
/// La règle : avant la partie, le joueur remet autant de crédits qu'il veut ; pendant la partie,
/// TOUTE baisse du compteur est un continue, sauf si elle accompagne l'arrivée d'un joueur 2 ou
/// plus. Ce qui distingue un continue d'une nouvelle partie, c'est le score : une nouvelle partie
/// le remet à zéro, un continue le garde. Un crédit consommé alors que le score est nul n'a rien à
/// protéger (départ d'une partie, ou continue sans le moindre point) : il ne coupe rien.
///
/// Sans ligne CREDITS au .MEM, rien ne coupe : le doute profite au joueur.
/// </summary>
public static class ContinuesParCredits
{
    /// <summary>L'écart toléré entre le START d'un joueur et le crédit qu'il consomme : 3 s.</summary>
    public const long FenetreArrivee = 180;

    /// <summary>Le délai où le score d'une nouvelle partie retombe : 6 s, comme en direct.</summary>
    public const long FenetreRemise = 360;

    public sealed record Bilan(IReadOnlyList<long> Coupes, bool PlusieursJoueurs);

    public static Bilan Calculer(
        IReadOnlyList<EvenementDeCredit> credits,
        IReadOnlyList<(long frame, long total)> lectures,
        IReadOnlyList<DepartDeJoueur> departs)
    {
        var coupes = new List<long>();
        var plusieurs = false;
        int? precedent = null;
        foreach (var credit in credits.OrderBy(c => c.Frame))
        {
            if (precedent is { } avant && credit.Value < avant)
            {
                switch (Nature(credit.Frame, lectures, departs))
                {
                    case Debit.ArriveeDUnJoueur:
                        plusieurs = true;
                        break;
                    case Debit.Continue:
                        coupes.Add(credit.Frame);
                        break;
                }
            }

            precedent = credit.Value;
        }

        return new Bilan(coupes, plusieurs);
    }

    public enum Debit
    {
        /// <summary>Départ d'une partie, ou continue sans point : rien à couper.</summary>
        Rien,
        Continue,
        ArriveeDUnJoueur,
    }

    /// <summary>Ce que vaut un crédit consommé à cette frame.</summary>
    public static Debit Nature(long frame, IReadOnlyList<(long frame, long total)> lectures, IReadOnlyList<DepartDeJoueur> departs)
    {
        if (departs.Any(d => d.Player >= 2 && Math.Abs(d.Frame - frame) <= FenetreArrivee))
        {
            return Debit.ArriveeDUnJoueur;
        }

        long scoreAvant = 0;
        foreach (var (f, total) in lectures)
        {
            if (f > frame) break;
            scoreAvant = total;
        }

        if (scoreAvant <= 0)
        {
            return Debit.Rien;
        }

        // Le score retombe juste après : une nouvelle partie commence, ce n'était pas un continue.
        var retombe = lectures.Any(l => l.frame > frame && l.frame <= frame + FenetreRemise && l.total < scoreAvant);
        return retombe ? Debit.Rien : Debit.Continue;
    }
}
