using System.Globalization;
using static RetroBat.Api.Leaderboard.LeaderboardOverlayService;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// Le score a battre au depart d'un defi de World Scoring : <c>Rang</c> et <c>Valeur</c> de la ligne visee, qui la tient,
/// et si c'est le record du joueur lui-meme.
/// </summary>
public sealed record CibleDuScore(int Rang, string Joueur, string Pays, long Valeur, bool EstMoi);

/// <summary>
/// Ce que l'ecran de depart du World Scoring sait au lancement. <c>Mode</c> : le libelle du classement (« 1CC ») ;
/// <c>Record</c>, <c>MonRang</c> : le record perso et sa place ; <c>Classes</c> : les joueurs classes lus ; <c>Prevol</c> :
/// la cle du dernier bandeau du scoring (scoring_certifiable...), vide tant qu'il n'est pas venu ; <c>Tableau</c> : faux
/// quand le classement n'a pas pu etre lu (hors ligne).
/// </summary>
public sealed record DepartDuScore(
    string Cle,
    string Jeu,
    string? Fanart,
    string Mode,
    bool Direct,
    string Joueur,
    string Pays,
    long? Record,
    int? MonRang,
    int Classes,
    CibleDuScore? Cible,
    string Prevol = "",
    bool Tableau = true,
    DateTime PrevolUtc = default);

/// <summary>Une ligne de stats du PlayerDeck pour le bilan (recipe.recap du theme), deja ecrite par le Deck.</summary>
public sealed record LigneDuDeck(string Libelle, string Valeur);

/// <summary>
/// Ce que le bilan du World Scoring sait a la fin de la partie, puis au verdict du site. <c>Score</c> : le score certifie
/// s'il est connu (verdict, ou bandeau de continue), sinon le dernier score lu ; <c>RangProvisoire</c> : la place de ce
/// score dans le classement lu au depart ; <c>RangAuDessus</c>, <c>EcartAuDessus</c> : la ligne juste au-dessus ;
/// <c>Sommet</c> : le score vise par la barre (le record du monde du mode, sinon le haut du classement lu) ;
/// <c>Verdict</c> : le statut du site (published, held...), vide tant qu'il n'est pas venu ; <c>Prevol</c> : le dernier
/// bandeau du scoring (une partie devenue non certifiable, rien de mesure, hors ligne).
/// </summary>
public sealed record BilanDuScore(
    string Cle,
    string Jeu,
    string? Fanart,
    string Mode,
    string Joueur,
    long? Score,
    long? RecordAvant,
    int? RangProvisoire,
    int? Classes,
    int? RangAuDessus,
    long? EcartAuDessus,
    long? Sommet,
    int SommetRang,
    long? DureeMs,
    int? ViesPerdues,
    long? Continues,
    string Verdict,
    int? RangCertifie,
    string Prevol,
    string DeckNom,
    IReadOnlyList<LigneDuDeck> Deck);

/// <summary>
/// LES ECRANS DU WORLD SCORING DANS LA FAMILLE MULTIPLAYER (decision P-D7 du 10/11, choix (a) du user : les cartes quittent
/// le pixel du jeu pour la famille NelfePlay). Pur : compose ce que montrent l'ecran de lancement (le VS) et l'ecran de
/// fin, sans rien toucher a l'ecran ni au score.
/// <list type="bullet">
/// <item>Depart : le mode au centre a la place de « VS », penche, a l'ombre de couleur ; le joueur a gauche (P1, record,
/// place, etat de la certification) ; le score a battre a droite (le rang vise a la place de « P2 ») ; les regles dans le
/// coin du conseil ; la frise des etapes ; « YOUR TURN! » quand le jeu est a l'ecran.</item>
/// <item>Bilan : le verdict en titre a la place de « VICTOIRE ! » ; le score et le rang (provisoire, puis certifie) a la
/// place du vainqueur ; la barre de progression vers le record ; la carte du joueur (ecart au record perso et au rang
/// au-dessus, duree, continues) et celle du PlayerDeck (ses stats).</item>
/// </list>
/// </summary>
public static class WorldScoringEcrans
{
    /// <summary>Le style des ecrans du World Scoring : celui des cartes de reference du MULTIPLAYER (Bangers, ombre de couleur).</summary>
    public const string Type = StylesDesEcrans.Combat;

    /// <summary>Le logo en haut a gauche : celui du World Scoring, comme MULTIPLAYER et CHALLENGES ont le leur.</summary>
    public const string Logo = "nelfeplay-worldscoring.png";

    private static readonly NumberFormatInfo Milliers = new() { NumberGroupSeparator = " ", NumberGroupSizes = [3] };

    /// <summary>Un score, ses milliers separes par une espace (« 1 310 900 »), comme sur tous les ecrans MULTIPLAYER.</summary>
    public static string Score(long v) => v.ToString("#,0", Milliers);

    /// <summary>Une duree en m:ss, h:mm:ss au-dela d'une heure.</summary>
    public static string Duree(long ms)
    {
        var s = Math.Max(0, ms / 1000);
        return s >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{s / 60}:{s % 60:00}");
    }

    private static string Rang(int r) => "#" + r.ToString(CultureInfo.InvariantCulture);

    /// <summary>Le libelle court des regles du mode (l'etiquette jaune sous le mot) et leur phrase (le coin du conseil).</summary>
    internal static (string Etiquette, string Phrase) Regles(string mode, Func<string, string, string> t)
    {
        var famille = mode.Split(' ')[0].ToUpperInvariant();
        return famille switch
        {
            "1CC" => (t("ws.screen.rule.1cc", "ONE CREDIT"), t("ws.screen.rules.1cc", "One credit. Your score counts until the first continue. No save state.")),
            "1LC" => (t("ws.screen.rule.1lc", "FIRST LIFE"), t("ws.screen.rules.1lc", "First life only: your score at the first miss counts. No save state.")),
            _ => (t("ws.screen.rule.best", "BEST SCORE"), t("ws.screen.rules.best", "Your best score counts. No save state.")),
        };
    }

    /// <summary>
    /// L'etat de la certification, d'apres le dernier bandeau du scoring : la pastille de la carte du joueur, avec la meme
    /// grammaire que l'etat d'un joueur au VS (coche verte, roue qui tourne, croix rouge).
    /// </summary>
    internal static (EtatAuLancement Etat, string Texte) Certification(string prevol, Func<string, string, string> t) => prevol switch
    {
        "scoring_certifiable" or "scoring_offline_certifiable" => (EtatAuLancement.Pret, t("ws.screen.state.certifiable", "CERTIFIABLE")),
        "scoring_not_certifiable" or "scoring_uncertified" or "scoring_credits_muted" => (EtatAuLancement.Deconnecte, t("ws.screen.state.uncertified", "UNCERTIFIED")),
        "scoring_nothing_measured" or "scoring_none_measured" => (EtatAuLancement.Deconnecte, t("ws.screen.state.no_score", "NO SCORE")),
        "scoring_emulator_pending" or "scoring_settings_pending" => (EtatAuLancement.EnRoute, t("ws.screen.state.pending", "PENDING")),
        "scoring_offline" => (EtatAuLancement.EnRoute, t("ws.screen.state.offline", "OFFLINE")),
        _ => (EtatAuLancement.Synchronisation, t("ws.screen.state.checking", "CHECKING…")),
    };

    /// <summary>
    /// L'ECRAN DE DEPART. <paramref name="jeuPret"/> : le jeu est a l'ecran (etape 2) ; <paramref name="depart"/> : « YOUR
    /// TURN! » est donne.
    /// </summary>
    public static EcranDeLancement ComposerLeDepart(DepartDuScore d, Func<string, string, string> t, bool jeuPret, DateTime? depart,
        string glypheQuitter = "b", string glypheValider = "a")
    {
        var (etiquette, phrase) = Regles(d.Mode, t);
        var (etat, texteEtat) = Certification(d.Prevol, t);
        var joueur = new JoueurDeLancement(
            1,
            d.Joueur,
            t("ws.screen.challenger", "CHALLENGER"),
            null,
            d.Pays,
            "",
            "",
            t("ws.screen.best", "BEST"),
            d.Record is { } record ? Score(record) : "--",
            t("ws.screen.rank", "RANK"),
            d.MonRang is { } rang ? Rang(rang) + (d.Classes > 0 ? " / " + d.Classes.ToString(CultureInfo.InvariantCulture) : "") : "--",
            etat,
            texteEtat,
            null,
            null,
            d.PrevolUtc == default ? DateTime.UtcNow : d.PrevolUtc,
            IconeVictoires: "trophee",
            IconeSerie: "chevrons");

        return new EcranDeLancement(
            "ws-" + d.Cle,
            Type,
            d.Mode.Length > 0 ? d.Mode : "SCORE",
            d.Jeu,
            t("ws.screen.header", "RANKED RUN"),
            "WORLD SCORING",
            d.Fanart,
            [joueur],
            d.Direct ? etiquette + " · LIVE" : etiquette,
            t("ws.screen.rules", "RULES"),
            phrase,
            jeuPret || depart is not null ? t("ws.screen.ready", "The game is on screen!") : t("ws.screen.loading", "The game is loading…"),
            [
                t("ws.screen.step.board", "BOARD READY"),
                t("ws.screen.step.loading", "LOADING THE GAME"),
                t("ws.screen.step.go", "GO!"),
            ],
            jeuPret || depart is not null ? 2 : 1,
            t("ws.screen.go", "YOUR TURN!"),
            depart,
            false,
            "",
            t("ws.screen.quit", "QUIT"),
            t("ws.screen.hold", "HOLD"),
            glypheValider,
            glypheQuitter,
            SortiePossible: true,
            Logo: Logo,
            Cible: CarteDe(d, t));
    }

    /// <summary>
    /// La carte du score a battre : la ligne visee (juste au-dessus du record perso, sinon la place a prendre pour entrer
    /// dans le top), le record du joueur s'il est deja premier, la premiere place a prendre sur un classement vide.
    /// </summary>
    internal static CarteDeLaCible CarteDe(DepartDuScore d, Func<string, string, string> t)
    {
        var pastille = t("ws.screen.to_beat", "SCORE TO BEAT");
        var joueurs = d.Classes > 0
            ? new ChiffreCle("trophee", t("ws.screen.players", "PLAYERS"), d.Classes.ToString(CultureInfo.InvariantCulture))
            : null;
        if (!d.Tableau)
            return new CarteDeLaCible("", pastille, t("ws.screen.offline", "BOARD OFFLINE"), "--", "", "", []);
        if (d.Cible is not { } c)
            return new CarteDeLaCible("#1", pastille, t("ws.screen.first", "FIRST SCORE TAKES IT"), "#1", "", "", joueurs is null ? [] : [joueurs], Couronne: true);

        if (c.EstMoi)
            return new CarteDeLaCible(Rang(c.Rang), pastille, t("ws.screen.your_record", "YOUR RECORD"), Score(c.Valeur), d.Joueur, d.Pays,
                joueurs is null ? [] : [joueurs], Couronne: c.Rang == 1);

        var chiffres = new List<ChiffreCle>();
        if (d.Record is { } record && c.Valeur > record)
            chiffres.Add(new ChiffreCle("chevrons", t("ws.screen.gap", "FROM YOUR BEST"), "+" + Score(c.Valeur - record)));
        if (joueurs is not null) chiffres.Add(joueurs);
        return new CarteDeLaCible(Rang(c.Rang), pastille,
            c.Rang == 1 ? t("ws.screen.world_record", "WORLD RECORD") : t("ws.screen.target", "RANK TARGET"),
            Score(c.Valeur), c.Joueur, c.Pays, chiffres, Couronne: c.Rang == 1);
    }

    // ── Le bilan ────────────────────────────────────────────────────────────────

    /// <summary>Ce que devient le score : le titre du bilan et la phrase d'etat (en haut a droite, sous l'en-tete).</summary>
    internal static (string Titre, string Etat, bool Classe, bool NonClasse) Verdict(BilanDuScore b, Func<string, string, string> t)
    {
        var certifie = b.Verdict == "published";
        var retenu = b.Verdict is "held" or "quarantined";
        var refuse = b.Verdict.Length > 0 && !certifie && !retenu;
        var nonCertifiable = b.Prevol is "scoring_not_certifiable" or "scoring_uncertified" or "scoring_credits_muted";
        var rien = b.Prevol is "scoring_nothing_measured" or "scoring_none_measured" || b.Score is null;
        var horsLigne = b.Prevol is "scoring_offline" or "scoring_offline_certifiable";
        var enAttente = b.Prevol is "scoring_emulator_pending" or "scoring_settings_pending";
        var record = b.Score is { } s && (b.RecordAvant is not { } avant ? s > 0 : s > avant);
        var rang = b.RangCertifie ?? b.RangProvisoire;
        var titre = certifie && b.RangCertifie == 1 ? t("ws.screen.title.world_record", "NEW RECORD!")
            : refuse || nonCertifiable || rien ? t("ws.screen.title.not_ranked", "NOT RANKED")
            : record ? t("ws.screen.title.best", "NEW BEST!")
            : certifie ? t("ws.screen.title.ranked", "SCORE RANKED")
            : retenu ? t("ws.screen.title.held", "ON HOLD")
            : t("ws.screen.title.over", "GAME OVER");
        var etat = certifie ? (b.RangCertifie is { } rc ? t("ws.screen.status.certified_rank", "Certified: rank {rank}.").Replace("{rank}", Rang(rc)) : t("ws.screen.status.certified", "Certified."))
            : retenu ? t("ws.screen.status.held", "On hold for review: not ranked yet.")
            : refuse ? t("ws.screen.status.refused", "Not ranked ({status}).").Replace("{status}", b.Verdict)
            : nonCertifiable ? t("ws.screen.status.uncertified", "Uncertified run: not ranked.")
            : rien ? t("ws.screen.status.nothing", "No score measured: not ranked.")
            : horsLigne ? t("ws.screen.status.offline", "Offline: score kept, sent later.")
            : enAttente ? t("ws.screen.status.pending", "Score kept, ranked once recognized.")
            : rang is not null ? t("ws.screen.status.provisional", "Provisional rank: score sent for certification.")
            : t("ws.screen.status.sent", "Score sent for certification.");
        return (titre, etat, certifie, refuse || nonCertifiable || rien);
    }

    /// <summary>L'icone d'une ligne de stats du Deck, d'apres son libelle (l'icone ne dit que la famille de la donnee).</summary>
    internal static string IconeDuDeck(string libelle)
    {
        var l = libelle.ToUpperInvariant();
        if (l.Contains("LOST") || l.Contains("DEATH") || l.Contains("MISS")) return "crane";
        if (l.Contains("BOMB") || l.Contains("HYPER")) return "eclair";
        if (l.Contains("MEDAL") || l.Contains("ITEM") || l.Contains("GEM")) return "diamant";
        if (l.Contains("STAGE") || l.Contains("ROUND") || l.Contains("LEVEL") || l.Contains("SECTION")) return "chevrons";
        if (l.Contains("TIME")) return "chrono";
        if (l.Contains("LIFE") || l.Contains("LIVES")) return "coeur";
        return "etoile";
    }

    /// <summary>L'ECRAN DE BILAN : le titre du verdict, le score et le rang, la barre vers le record, les cartes.</summary>
    public static EcranDeFin ComposerLeBilan(BilanDuScore b, Func<string, string, string> t, string glypheRejouer = "a", string glypheQuitter = "b")
    {
        var (titre, etat, classe, nonClasse) = Verdict(b, t);
        // Un score qui ne sera pas classe (partie non certifiable, refusee, rien de mesure) ne montre aucun rang.
        var rang = nonClasse ? null : b.RangCertifie ?? b.RangProvisoire;
        var valeur = b.Score is { } s ? Score(s) : "--";
        var premier = rang == 1 && b.Score is > 0;
        var mode = b.Mode.Length > 0 ? b.Mode : "SCORE";
        var ruban = mode + (rang is { } r ? " · " + Rang(r) : "");
        var second = rang is null ? "" : classe ? t("ws.screen.certified", "CERTIFIED") : t("ws.screen.provisional", "PROVISIONAL");

        // La barre : du score du joueur au record (du monde du mode, sinon le haut du classement lu). Sans classement, le score
        // classe est le premier : il est lui-meme le sommet.
        ProgressionDeFin? progression = null;
        if (b.Score is { } score)
        {
            var sommet = b.Sommet ?? score;
            var atteint = score >= sommet;
            progression = new ProgressionDeFin(
                b.SommetRang == 1 ? t("ws.screen.to_record", "TO THE WORLD RECORD") : t("ws.screen.to_top", "TO THE TOP OF THE BOARD"),
                valeur,
                rang is { } rg ? Rang(rg) : "",
                Score(sommet),
                Rang(Math.Max(1, b.SommetRang)),
                sommet > 0 ? Math.Clamp((double)score / sommet, 0, 1) : 1,
                atteint);
        }

        // La carte du joueur : l'ecart au record perso sous son nom (vert, dore s'il le bat), puis l'ecart au rang au-dessus,
        // la duree et les continues ; rien d'invente : une donnee absente n'a pas de ligne, un verdict attendu montre « - ».
        var ecartPerso = b is { Score: { } sc, RecordAvant: { } av } && sc != av ? (sc > av ? "+" : "-") + Score(Math.Abs(sc - av)) : "";
        var mesures = new List<MesureDeFin>();
        if (b is { RangAuDessus: { } dessus, EcartAuDessus: > 0 })
            mesures.Add(new MesureDeFin("trophee", t("ws.screen.to_rank", "TO {rank}").Replace("{rank}", Rang(dessus)), b.EcartAuDessus, -1, false, Milliers: true));
        if (b.DureeMs is > 0)
            mesures.Add(new MesureDeFin("chrono", t("ws.screen.play_time", "PLAY TIME"), b.DureeMs, -1, false, Texte: Duree(b.DureeMs.Value)));
        mesures.Add(new MesureDeFin("chevrons", t("ws.screen.continues", "CONTINUES"), b.Continues, -1, b.Continues == 0));
        if (b.ViesPerdues is { } vies && mesures.Count < 3)
            mesures.Add(new MesureDeFin("coeur", t("ws.screen.lives_lost", "LIVES LOST"), vies, -1, vies == 0));

        var carteJoueur = new JoueurDeFin(1, b.Joueur, "", null, "", b.RecordAvant is { } ra ? Score(ra) : "", ecartPerso, false, rang, "",
            mesures.Take(3).ToList(), PrefixeCote: t("ws.screen.best", "BEST") + " ", EcartDore: ecartPerso.StartsWith('+'));
        var cartes = new List<JoueurDeFin> { carteJoueur };
        var deck = b.Deck.Select(l => new MesureDeFin(IconeDuDeck(l.Libelle), l.Libelle.ToUpperInvariant(), null, -1, false, Texte: l.Valeur)).ToList();
        if (b.ViesPerdues is { } perdues && !mesures.Take(3).Any(m => m.Icone == "coeur") && !deck.Any(m => m.Icone == "crane"))
            deck.Insert(0, new MesureDeFin("coeur", t("ws.screen.lives_lost", "LIVES LOST"), perdues, -1, perdues == 0));
        if (deck.Count > 0)
            cartes.Add(new JoueurDeFin(2, "PLAYER DECK", b.DeckNom.ToUpperInvariant(), null, "", "", "", false, null, "", deck.Take(3).ToList(), Pastille: ""));

        return new EcranDeFin(
            // Le verdict change la cle : le titre se rejoue quand le score est classe.
            "ws-bilan-" + b.Cle + "-" + (b.Verdict.Length > 0 ? b.Verdict : "provisoire"),
            Type,
            titre,
            b.Jeu,
            b.DureeMs is > 0 ? Duree(b.DureeMs.Value) : "",
            b.Fanart,
            cartes,
            null,
            etat,
            "",
            new TextesDeFin(t("ws.screen.over", "RUN OVER"), "", t("ws.screen.phrase", "GG! One more run is one button away."), t("ws.screen.hold", "HOLD"),
                t("ws.screen.retry", "RETRY"), t("ws.screen.quit", "QUIT")),
            glypheRejouer,
            glypheQuitter,
            MonScore: new ScoreDeFin(mode + " " + t("ws.screen.score", "SCORE"), valeur, ruban, second, premier),
            Logo: Logo,
            Progression: progression);
    }
}
