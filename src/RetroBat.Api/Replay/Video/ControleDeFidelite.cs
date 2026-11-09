namespace RetroBat.Api.Replay.Video;

/// <summary>
/// Un score certifie et la fin de son run dans le replay (frames du replay), que le site joint a la cible d'un film
/// (<c>checks</c>). Un replay peut en porter plusieurs : le 1CC et le 1LC d'une meme partie ne finissent pas au meme endroit.
/// </summary>
public sealed record PointDuScore(long FinFrame, long Score);

/// <summary>
/// LE CONTROLE DE FIDELITE D'UN FILM RENDU DEPUIS UN REPLAY (decision du user du 09/11). Les dix films faux de la station
/// avaient le bon score a la fin : RetroArch recalait l'etat toutes les 5 s (<c>replay_checkpoint_deserialize</c>), et entre
/// deux recalages les commandes derivaient. Le rendu se fait desormais SANS recalage, et le score lu dans la memoire du coeur
/// pres de la fin du run doit etre le score certifie. Sinon le film ne part pas.
/// <list type="bullet">
/// <item>fidele : le score certifie est lu dans la fenetre qui precede la fin du run (<see cref="FenetreS"/>) ;</item>
/// <item>desync : des lectures dans la fenetre, et le score certifie n'y est pas, sans meme y etre franchi entre deux
/// lectures : le replay ne rejoue pas la partie ;</item>
/// <item>non_verifie : aucune lecture utile (score illisible sous ce coeur, pas de point du site, fin hors du film), ou un
/// score franchi entre deux lectures sans etre lu (doute).</item>
/// </list>
/// La fin d'un run (BornesVideo) porte 3 s de marge apres la mort ou le continue : pendant cette marge, et pendant l'animation
/// de la mort, le score ne bouge plus ; les lectures, toutes les 200 ms environ, le voient. Pur, pour etre teste.
/// </summary>
public static class ControleDeFidelite
{
    public const string Fidele = "fidele";
    public const string Desync = "desync";
    public const string NonVerifie = "non_verifie";

    /// <summary>La fenetre de lecture avant la fin d'un run, en secondes.</summary>
    public const double FenetreS = 15;

    /// <summary>Le verdict et son detail lisible (journal, raison envoyee au site).</summary>
    public sealed record Verdict(string Issue, string Detail);

    /// <param name="points">Les scores certifies du replay et la fin de leur run.</param>
    /// <param name="releves">Les lectures du score pendant le rendu (frame du replay, score compose), dans l'ordre.</param>
    /// <param name="fps">La cadence du replay.</param>
    public static Verdict Juger(IReadOnlyList<PointDuScore>? points, IReadOnlyList<(long Frame, long Score)>? releves, double fps)
    {
        if (points is not { Count: > 0 }) return new(NonVerifie, "aucun score certifie joint");
        if (releves is not { Count: > 0 }) return new(NonVerifie, "score illisible pendant le rendu");
        var ips = fps > 1 ? fps : 60;
        var fenetre = (long)Math.Round(FenetreS * ips);
        var verifies = new List<string>();
        var doutes = new List<string>();
        foreach (var p in points)
        {
            var dans = releves.Where(r => r.Frame >= p.FinFrame - fenetre && r.Frame <= p.FinFrame).OrderBy(r => r.Frame).ToList();
            if (dans.Count == 0) continue;
            if (dans.Any(r => r.Score == p.Score))
            {
                verifies.Add(p.Score + " lu avant la frame " + p.FinFrame);
                continue;
            }
            var franchi = false;
            for (var i = 1; i < dans.Count && !franchi; i++)
                franchi = dans[i - 1].Score < p.Score && p.Score < dans[i].Score;
            if (franchi)
            {
                doutes.Add(p.Score + " franchi entre deux lectures avant la frame " + p.FinFrame);
                continue;
            }
            return new(Desync, "score certifie " + p.Score + ", lu " + string.Join("/", dans.Select(r => r.Score).Distinct().Take(6))
                + " entre les frames " + dans[0].Frame + " et " + dans[^1].Frame);
        }
        if (verifies.Count > 0) return new(Fidele, string.Join(" ; ", verifies.Concat(doutes)));
        return new(NonVerifie, doutes.Count > 0 ? string.Join(" ; ", doutes) : "aucune lecture pres de la fin du run");
    }
}
