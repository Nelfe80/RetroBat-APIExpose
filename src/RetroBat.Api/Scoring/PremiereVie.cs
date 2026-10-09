namespace RetroBat.Api.Scoring;

/// <summary>
/// LE 1LC, SCORE DE LA PREMIERE VIE (decision user du 2026-10-09). Des qu'une vie est perdue, le score
/// du moment devient le 1LC ; la meme partie continue de concourir pour le 1CC jusqu'au premier
/// credit. Un seul replay pour les deux : la lecture du 1LC s'arrete a la premiere mort.
///
/// La coupure se prend dans le run retenu pour le 1CC : la premiere perte de vie du joueur 1 a partir
/// de sa premiere lecture. Les lectures jusqu'a elle comprise restent (un mort ne marque plus) ; sans
/// perte dans le run, le 1LC est le run entier. Le signal est celui du bloc lives du .MEM : un jeu
/// n'ouvre son 1LC qu'une fois ce bloc qualifie au labo, une fausse mort couperait le joueur trop tot.
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
}
