using RetroBat.Api.Leaderboard;
using static RetroBat.Api.Leaderboard.LeaderboardOverlayService;

namespace RetroBat.Api.Speedrun;

/// <summary>Ce qu'il advient du temps d'une seance (CDC du panneau, §8.2).</summary>
public enum IssueDuBilan
{
    Classe,
    EnVerification,
    Retenu,
    Refuse,
    NonEnvoye,
    AucunTemps,
}

/// <summary>
/// Ce que la page de bilan sait a la fermeture du jeu. <c>Temps</c> : le meilleur de la seance, en images. <c>Rang</c>,
/// <c>Record</c> et <c>Classes</c> : la reponse du site. <c>Motif</c> : la raison d'un temps retenu, refuse ou absent.
/// <c>Finies</c> : les tentatives allees jusqu'a l'arrivee.
/// </summary>
public sealed record BilanDEpreuve(Reglement Defi, string Jeu, string Joueur, string? Fanart, IssueDuBilan Issue, long? Temps, int Tentatives,
    int Finies = 0, int? Rang = null, long? Record = null, int? Classes = null, string Motif = "", IReadOnlyList<TempsClasse>? Classement = null);

/// <summary>
/// LES PAGES PLEIN ECRAN D'UNE EPREUVE (CDC du panneau, §8), sur les patrons MULTIPLAYER : la preparation reprend l'ecran de
/// lancement, le bilan l'ecran de fin. Rien ici ne touche l'ecran : on compose ce qu'ils montrent.
/// </summary>
public sealed partial class DefisPanelService
{
    /// <summary>Le mot central (decision P12) : il ne se traduit pas, comme les onglets.</summary>
    internal static string MotDe(Reglement defi) => EstUnSpeedrun(defi) ? "SPEEDRUN" : "CHALLENGE";

    private const string LogoDesPages = "nelfeplay-challenges.png";

    internal EcranDeLancement ComposerLaPreparation(Reglement defi, string jeu, string? fanart, bool jeuPret, string langue) => new(
        "epreuve-" + defi.ChallengeId,
        StylesDesEcrans.Course,
        MotDe(defi),
        jeu,
        T("speedrun.page.ranked", "RANKED ATTEMPT"),
        defi.Titre(langue),
        fanart,
        [],
        "",
        "",
        "",
        T("speedrun.page.loading", "The game is loading…"),
        [
            T("speedrun.page.step.ready", "ATTEMPT READY"),
            T("speedrun.page.step.loading", "LOADING THE GAME"),
            T("speedrun.page.step.go", "IT'S ON!"),
        ],
        jeuPret ? 2 : 1,
        T("speedrun.page.go", "YOUR TURN!"),
        null,
        false,
        "",
        T("speedrun.page.quit", "QUIT"),
        T("speedrun.page.hold", "HOLD"),
        _glypheValider,
        _glypheAnnuler,
        SortiePossible: true,
        Fiche: Fiche(defi),
        Logo: LogoDesPages);

    internal EcranDeFin ComposerLeBilan(BilanDEpreuve b, string langue)
    {
        var premier = b.Issue == IssueDuBilan.Classe && b.Rang == 1;
        // Les barres disent quelque chose : la part des tentatives finies, et la place parmi les joueurs classes.
        var mesures = new List<MesureDeFin>
        {
            new("chevrons", T("speedrun.page.attempts", "ATTEMPTS"), b.Tentatives, 1, false),
            new("chrono", T("speedrun.page.finished", "FINISHED"), b.Finies, b.Tentatives > 0 ? (double)b.Finies / b.Tentatives : 0, false),
        };
        if (b.Classes is { } classes)
        {
            var part = b.Rang is { } r && classes > 1 ? 1 - (r - 1d) / (classes - 1) : 1;
            mesures.Add(new MesureDeFin("trophee", T("speedrun.page.players", "PLAYERS"), classes, part, premier));
        }

        var valeur = b.Temps is { } temps ? Chronometrage.Afficher(temps, b.Defi.Cadence) : SansTemps;
        var ruban = MotDe(b.Defi) + (b.Rang is { } place ? " · #" + place : "");
        var ecart = b is { Temps: { } t, Record: { } record } && t > record
            ? "+" + Chronometrage.Afficher(t - record, b.Defi.Cadence) + " " + T("speedrun.page.from_record", "from the record")
            : "";
        return new EcranDeFin(
            "bilan-" + b.Defi.ChallengeId,
            StylesDesEcrans.Course,
            TitreDuBilan(b.Issue, premier),
            b.Jeu,
            "",
            b.Fanart,
            // Jamais « vainqueur » : l'ecran de fin montrerait le joueur a la place de son temps.
            [new JoueurDeFin(1, b.Joueur, "", null, "", "", "", false, b.Rang, "", mesures)],
            null,
            b.Motif,
            "",
            new TextesDeFin(T("speedrun.page.over", "ATTEMPT OVER"), "", "", T("speedrun.page.hold", "HOLD"), T("speedrun.page.retry", "RETRY"), T("speedrun.page.quit", "QUIT")),
            _glypheValider,
            _glypheAnnuler,
            MonScore: new ScoreDeFin(b.Defi.Titre(langue), valeur, ruban, ecart, premier),
            Logo: LogoDesPages,
            Classement: AutourDe(b.Classement ?? [], b.Rang)
                .Select(l => new LigneDeDefi($"{l.Rang}   {l.Joueur}", "", Chronometrage.Afficher(l.Valeur, b.Defi.Cadence), Marque: l.Rang == b.Rang ? "moi" : ""))
                .ToList());
    }

    /// <summary>Le record, puis la place du joueur et ses voisins : le joueur voit qui il suit et qui il devance.</summary>
    internal static IReadOnlyList<TempsClasse> AutourDe(IReadOnlyList<TempsClasse> lignes, int? rang, int nombre = 5)
    {
        if (lignes.Count <= nombre) return lignes;
        var fenetre = nombre - 1;
        var debut = Math.Clamp((rang ?? 1) - 1 - fenetre / 2, 1, lignes.Count - fenetre);
        return [lignes[0], .. lignes.Skip(debut).Take(fenetre)];
    }

    private string TitreDuBilan(IssueDuBilan issue, bool premier) => issue switch
    {
        IssueDuBilan.Classe when premier => T("speedrun.page.title.record", "NEW RECORD!"),
        IssueDuBilan.Classe => T("speedrun.page.title.ranked", "TIME RANKED"),
        IssueDuBilan.EnVerification => T("speedrun.page.title.checking", "TIME SENT"),
        IssueDuBilan.Retenu => T("speedrun.page.title.held", "TIME ON HOLD"),
        IssueDuBilan.Refuse => T("speedrun.page.title.refused", "TIME REFUSED"),
        IssueDuBilan.NonEnvoye => T("speedrun.page.title.not_sent", "NOT SENT"),
        _ => T("speedrun.page.title.no_time", "NO TIME"),
    };
}
