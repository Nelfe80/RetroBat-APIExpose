namespace RetroBat.Api.Scoring;

/// <summary>
/// LES BORNES DE LA PERFORMANCE DANS SON REPLAY (CDC video V2, §4.4, 2026-10-14). Le rendu video d'un
/// score ne filme que la performance certifiee, pas tout l'enregistrement : il lui faut le debut et la fin
/// du run, en frames DU REPLAY. Elles appartiennent au score et non au replay (un meme replay porte un 1CC
/// et un 1LC qui ne finissent pas au meme endroit) : le manifeste scelle du replay n'est pas touche, elles
/// voyagent avec le lien score et replay.
///
/// La conversion est celle du 1LC (<c>arret_du_replay</c>) : frame du jeu moins la frame du jeu au depart
/// de l'enregistrement. Le compteur du wrapper avance d'une unite par image du coeur, comme celui du
/// replay ; le decalage vient du moment ou le depart est note, a quelques images pres. La qualite est
/// donc « bornee » : assez pour couper un film avec une marge, pas pour synchroniser image par image.
/// </summary>
public static class BornesVideo
{
    /// <summary>Ce qu'on garde apres la fin du run : la mort ou l'ecran de fin, environ trois secondes.</summary>
    public const long MargeDeFin = 180;

    /// <summary>
    /// Les bornes, en frames du replay, ou null quand on ne sait pas les poser (run vide, run hors de
    /// l'enregistrement). Debut : le depart de l'enregistrement, ou la derniere coupure (mort, continue)
    /// avant le run quand un autre run le precede dans le meme enregistrement, a defaut la derniere lecture
    /// de ce run precedent. Fin : la premiere coupure apres le dernier score du run, a defaut ce dernier
    /// score, plus <see cref="MargeDeFin"/>, sans depasser la fin de l'enregistrement.
    /// </summary>
    /// <param name="debutEnregistrement">Frame du jeu au depart de l'enregistrement.</param>
    /// <param name="finEnregistrement">Frame du jeu a sa fin, si elle est connue.</param>
    /// <param name="lectures">Toutes les lectures de score de la partie (frame du jeu, total).</param>
    /// <param name="run">Le run certifie (1CC) ou coupe a la premiere mort (1LC).</param>
    /// <param name="coupures">Les frames du jeu ou un run se ferme : morts comptees, continues, arrivees.</param>
    public static (long Debut, long Fin)? Calculer(
        long debutEnregistrement,
        long? finEnregistrement,
        IReadOnlyList<(long frame, long total)> lectures,
        IReadOnlyList<(long frame, long total)> run,
        IReadOnlyList<long> coupures)
    {
        if (run.Count == 0) return null;
        var premier = run[0].frame;
        var dernier = run[^1].frame;
        if (dernier < debutEnregistrement) return null;
        if (finEnregistrement is long finEnr && premier > finEnr) return null;

        // Le debut : un run precedent dans le meme enregistrement se voit a une lecture d'avant le premier
        // score du run. On demarre alors a la coupure qui les separe, ou a defaut a cette lecture.
        var debutJeu = debutEnregistrement;
        var precedentes = lectures.Where(l => l.frame >= debutEnregistrement && l.frame < premier).ToList();
        if (precedentes.Count > 0)
        {
            var derniereAvant = precedentes[^1].frame;
            var coupureAvant = coupures.Where(c => c > derniereAvant && c <= premier).DefaultIfEmpty(-1).Min();
            debutJeu = coupureAvant >= 0 ? coupureAvant : derniereAvant;
        }

        // La fin : la coupure qui ferme le run (la mort du 1LC, le continue du 1CC), sinon le dernier score.
        var coupureApres = coupures.Where(c => c >= dernier).DefaultIfEmpty(-1).Min();
        var finJeu = (coupureApres >= 0 ? coupureApres : dernier) + MargeDeFin;
        if (finEnregistrement is long fin && finJeu > fin) finJeu = fin;

        var debut = Math.Max(0, debutJeu - debutEnregistrement);
        var finReplay = finJeu - debutEnregistrement;
        return finReplay > debut ? (debut, finReplay) : null;
    }
}
