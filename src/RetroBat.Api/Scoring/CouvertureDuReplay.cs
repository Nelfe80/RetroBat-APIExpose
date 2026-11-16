namespace RetroBat.Api.Scoring;

/// <summary>
/// UN SCORE NE PART QU'AVEC UN REPLAY QUI MONTRE SON RUN (16/11, Old AF Gamer). Une partie peut porter plusieurs
/// enregistrements (arret, relance) ; le score se rattachait au dernier ouvert quand aucun ne contenait le pic du run. Le
/// record GigaWing de 58,4 milliards (14/11) est ainsi parti avec 33 s prises au debut d'un run de 6 min, et le 1LC de
/// Progear (15/11) avec un replay qui commencait apres la premiere vie perdue. Comme les epreuves depuis le 08/11 : sans
/// enregistrement qui couvre le run, le score part sans replay.
/// <list type="bullet">
/// <item>1CC : du depart du run jusqu'a son pic.</item>
/// <item>1LC : du depart du run jusqu'a la premiere vie perdue (le pic du run entier quand aucune vie n'est perdue).</item>
/// </list>
/// Les images sont celles du rapporteur (horloge du wrapper). Pur.
/// </summary>
public static class CouvertureDuReplay
{
    /// <summary>
    /// Le retard admis du debut de l'enregistrement sur le depart du run : cinq secondes a 60 images/s. RetroArch confirme
    /// l'enregistrement jusqu'a une seconde apres l'ordre sur une borne chargee, la boucle de l'enregistreur sonde toutes les
    /// 1,5 s, et le depart par le score (borne dont l'API ne lit pas les manettes) part apres la premiere montee.
    /// </summary>
    public const long ToleranceDuDepart = 300;

    /// <summary>
    /// Le depart du run : sa premiere montee (la lecture qui depasse la premiere), sinon sa premiere lecture. La premiere
    /// lecture peut etre le score pose au START (<c>AvecLeScoreAuDepart</c>), loin avant les premiers points ; la premiere
    /// montee est le premier instant que le replay doit montrer. Null pour un run vide.
    /// </summary>
    public static long? DepartDuRun(IReadOnlyList<(long frame, long total)> run)
    {
        if (run.Count == 0) return null;
        var premiere = run[0].total;
        foreach (var pt in run)
        {
            if (pt.total > premiere) return pt.frame;
        }
        return run[0].frame;
    }

    /// <summary>
    /// Le dernier enregistrement qui couvre <paramref name="depart"/> (a <see cref="ToleranceDuDepart"/> pres) jusqu'a
    /// <paramref name="fin"/> ; null si aucun. Un enregistrement encore ouvert (fin inconnue) couvre tout ce qui suit son debut.
    /// </summary>
    public static string? ReplayQuiCouvre(IReadOnlyList<(string Id, long Debut, long? Fin)> enregistrements, long depart, long fin)
    {
        string? choisi = null;
        foreach (var e in enregistrements)
        {
            if (e.Debut <= depart + ToleranceDuDepart && e.Debut <= fin && (e.Fin is null || e.Fin >= fin)) choisi = e.Id;
        }
        return choisi;
    }

    /// <summary>Le replay du run 1CC : du depart au pic. Null pour un run vide ou sans enregistrement qui le couvre.</summary>
    public static string? DuRun(IReadOnlyList<(string Id, long Debut, long? Fin)> enregistrements, IReadOnlyList<(long frame, long total)> run)
        => DepartDuRun(run) is { } depart ? ReplayQuiCouvre(enregistrements, depart, run[^1].frame) : null;

    /// <summary>
    /// Le replay du 1LC : du depart du run de la premiere vie (le meme depart que le 1CC, le 1LC en est le debut) jusqu'a la
    /// premiere vie perdue (<paramref name="mort"/>), ou jusqu'a son pic quand aucune n'est perdue.
    /// </summary>
    public static string? DeLaPremiereVie(IReadOnlyList<(string Id, long Debut, long? Fin)> enregistrements,
        IReadOnlyList<(long frame, long total)> runDeLaPremiereVie, long? mort)
        => DepartDuRun(runDeLaPremiereVie) is { } depart
            ? ReplayQuiCouvre(enregistrements, depart, mort ?? runDeLaPremiereVie[^1].frame)
            : null;
}
