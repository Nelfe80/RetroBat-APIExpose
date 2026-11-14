namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LA CADENCE D'UN MINUTEUR DE FOND VERS LE SITE (lot 4 bis C-9, 14/11) : aucun minuteur n'avait de recul apres un echec,
/// un seul avait de la gigue. Apres une coupure ou une sortie de version, toutes les bornes repartaient au meme rythme et
/// martelaient le site au pire moment. Ici : l'attente double a chaque echec d'affilee (jusqu'a un plafond) et se disperse
/// de plus ou moins 10 %. Pur.
/// </summary>
public static class CadenceDeFond
{
    /// <summary>
    /// L'attente avant le prochain passage : <paramref name="base_"/>, doublee <paramref name="echecs"/> fois (au plus
    /// <paramref name="plafond"/>, jamais moins que la base), puis dispersee de plus ou moins <paramref name="gigue"/>
    /// (<paramref name="alea"/> de 0 a 1).
    /// </summary>
    public static TimeSpan Prochaine(TimeSpan base_, int echecs, TimeSpan plafond, double alea, double gigue = 0.1)
    {
        var attente = base_;
        if (echecs > 0)
        {
            var recul = TimeSpan.FromTicks(base_.Ticks * (1L << Math.Clamp(echecs, 0, 16)));
            attente = recul > plafond ? (plafond > base_ ? plafond : base_) : recul;
        }

        var facteur = 1 - gigue + 2 * gigue * Math.Clamp(alea, 0, 1);
        return TimeSpan.FromTicks((long)(attente.Ticks * facteur));
    }

    /// <summary>Le meme, avec le hasard partage de l'API.</summary>
    public static TimeSpan Prochaine(TimeSpan base_, int echecs, TimeSpan plafond)
        => Prochaine(base_, echecs, plafond, Random.Shared.NextDouble());
}
