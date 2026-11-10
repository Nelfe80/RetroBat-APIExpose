using RetroBat.Api.Leaderboard;
using Xunit;
using E = RetroBat.Api.Leaderboard.LeaderboardOverlayService.EtatAuLancement;
using L = RetroBat.Api.Leaderboard.LeaderboardOverlayService;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES ECRANS DU WORLD SCORING DANS LA FAMILLE MULTIPLAYER (P-D7, choix (a) du user, 10/11) : ce que composent le depart et le
/// bilan, puis leur vrai rendu GDI+ (le code de l'ecran de lancement et de l'ecran de fin MULTIPLAYER) dans des PNG. Les
/// apercus ne se dessinent que si NELFEPLAY_APERCUS donne un dossier de sortie et NELFEPLAY_APERCUS_IMAGES /
/// NELFEPLAY_APERCUS_MEDIAS les images ; NELFEPLAY_APERCUS_FILM ajoute la suite d'images de l'animation (30 images/s).
/// </summary>
public class WorldScoringEcransTests
{
    private static readonly Func<string, string, string> Anglais = (_, parDefaut) => parDefaut;

    private static DepartDuScore Depart(string prevol = "scoring_certifiable") => new(
        "ket-1", "Ketsui", null, "1CC", false, "NELFE", "FR", 1_104_200, 16, 212, new CibleDuScore(3, "AKIRA", "JP", 1_310_900, false), prevol);

    private static BilanDuScore Bilan(string verdict = "", int? certifie = null) => new(
        "ket-1", "Ketsui", null, "1CC", "NELFE", 1_286_500, 1_104_200, 4, 212, 3, 24_400, 2_950_000, 1, 754_000, null, verdict.Length > 0 ? 0 : null,
        verdict, certifie, "scoring_certifiable", "Green Plate",
        [new("MAX CHIPS", "268"), new("BOMBS USED", "2"), new("SHIPS LOST", "1"), new("STAGE BONUS", "88 400")]);

    // ── Le depart ────────────────────────────────────────────────────────

    [Fact]
    public void Le_depart_met_le_mode_au_centre_le_joueur_a_gauche_et_le_score_a_battre_a_droite()
    {
        var l = WorldScoringEcrans.ComposerLeDepart(Depart(), Anglais, false, null);

        Assert.Equal("1CC", l.Mot);
        Assert.Equal(StylesDesEcrans.Combat, l.Type);
        Assert.True(L.FaceAFace(l));
        var joueur = Assert.Single(l.Joueurs);
        Assert.Equal(1, joueur.Place);
        Assert.Equal("NELFE", joueur.Pseudo);
        Assert.Equal("1 104 200", joueur.Victoires);
        Assert.Equal("#16 / 212", joueur.Serie);
        Assert.Equal(E.Pret, joueur.Etat);
        Assert.Equal("CERTIFIABLE", joueur.TexteDeLEtat);
        Assert.NotNull(l.Cible);
        Assert.Equal("#3", l.Cible!.Rang);
        Assert.Equal("1 310 900", l.Cible.Valeur);
        Assert.Equal("AKIRA", l.Cible.Joueur);
        Assert.Equal("RANK TARGET", l.Cible.Libelle);
        Assert.Contains(l.Cible.Chiffres, c => c.Valeur == "+206 700");
        Assert.Equal("ONE CREDIT", l.Etiquette);
        Assert.Equal("RULES", l.TitreDuConseil);
        Assert.Contains("first continue", l.Conseil);
        Assert.Equal(1, l.Etape);
        Assert.Null(l.DepartUtc);
        Assert.True(l.SortiePossible);
    }

    [Fact]
    public void Le_depart_dit_la_certification_avec_la_grammaire_de_l_etat_du_vs()
    {
        Assert.Equal(E.Synchronisation, WorldScoringEcrans.ComposerLeDepart(Depart(""), Anglais, false, null).Joueurs[0].Etat);
        Assert.Equal(E.Deconnecte, WorldScoringEcrans.ComposerLeDepart(Depart("scoring_not_certifiable"), Anglais, false, null).Joueurs[0].Etat);
        Assert.Equal(E.EnRoute, WorldScoringEcrans.ComposerLeDepart(Depart("scoring_settings_pending"), Anglais, false, null).Joueurs[0].Etat);
    }

    [Fact]
    public void Le_depart_passe_a_l_etape_du_jeu_puis_a_your_turn()
    {
        var pret = WorldScoringEcrans.ComposerLeDepart(Depart(), Anglais, true, null);
        Assert.Equal(2, pret.Etape);
        Assert.Equal("The game is on screen!", pret.Legende);
        var go = WorldScoringEcrans.ComposerLeDepart(Depart(), Anglais, true, DateTime.UtcNow);
        Assert.NotNull(go.DepartUtc);
        Assert.Equal("YOUR TURN!", go.AVous);
    }

    [Fact]
    public void La_cible_suit_le_classement_record_perso_premier_score_hors_ligne()
    {
        var premier = Depart() with { Record = 3_000_000, MonRang = 1, Cible = new CibleDuScore(1, "NELFE", "FR", 3_000_000, true) };
        var c = WorldScoringEcrans.CarteDe(premier, Anglais);
        Assert.Equal("YOUR RECORD", c.Libelle);
        Assert.True(c.Couronne);

        var vide = Depart() with { Record = null, MonRang = null, Classes = 0, Cible = null };
        Assert.Equal("FIRST SCORE TAKES IT", WorldScoringEcrans.CarteDe(vide, Anglais).Libelle);
        Assert.Equal("--", WorldScoringEcrans.ComposerLeDepart(vide, Anglais, false, null).Joueurs[0].Victoires);

        var horsLigne = Depart() with { Tableau = false };
        Assert.Equal("BOARD OFFLINE", WorldScoringEcrans.CarteDe(horsLigne, Anglais).Libelle);

        var record = Depart() with { Cible = new CibleDuScore(1, "AKIRA", "JP", 2_950_000, false) };
        Assert.Equal("WORLD RECORD", WorldScoringEcrans.CarteDe(record, Anglais).Libelle);
    }

    [Fact]
    public void Le_mode_donne_ses_regles()
    {
        Assert.Equal("FIRST LIFE", WorldScoringEcrans.Regles("1LC", Anglais).Etiquette);
        Assert.Equal("BEST SCORE", WorldScoringEcrans.Regles("NORMAL", Anglais).Etiquette);
        Assert.Equal("ONE CREDIT · LIVE", WorldScoringEcrans.ComposerLeDepart(Depart() with { Direct = true }, Anglais, false, null).Etiquette);
    }

    // ── Le bilan ─────────────────────────────────────────────────────────

    [Fact]
    public void Le_bilan_provisoire_dit_new_best_le_rang_provisoire_et_la_barre_vers_le_record()
    {
        var f = WorldScoringEcrans.ComposerLeBilan(Bilan(), Anglais);

        Assert.Equal("NEW BEST!", f.Titre);
        Assert.Equal("12:34", f.Duree);
        Assert.NotNull(f.MonScore);
        Assert.Equal("1 286 500", f.MonScore!.Valeur);
        Assert.Equal("1CC · #4", f.MonScore.Ruban);
        Assert.Equal("PROVISIONAL", f.MonScore.Second);
        Assert.Contains("Provisional", f.Note);
        var p = Assert.IsType<L.ProgressionDeFin>(f.Progression);
        Assert.Equal("TO THE WORLD RECORD", p.Libelle);
        Assert.Equal("1 286 500", p.Gauche);
        Assert.Equal("#4", p.RangGauche);
        Assert.Equal("2 950 000", p.Droite);
        Assert.Equal("#1", p.RangDroite);
        Assert.Equal(1_286_500d / 2_950_000, p.Part, 6);
        Assert.False(p.Atteint);

        Assert.Equal(2, f.Joueurs.Count);
        var joueur = f.Joueurs[0];
        Assert.Equal(4, joueur.Rang);
        Assert.Equal("1 104 200", joueur.Cote);
        Assert.Equal("+182 300", joueur.Ecart);
        Assert.True(joueur.EcartDore);
        Assert.Equal("BEST ", joueur.PrefixeCote);
        Assert.Contains(joueur.Mesures, m => m.Libelle == "TO #3" && m.Valeur == 24_400);
        Assert.Contains(joueur.Mesures, m => m.Libelle == "PLAY TIME" && m.Texte == "12:34");
        // Le verdict n'est pas la : les continues attendent (« - »), rien d'invente.
        Assert.Contains(joueur.Mesures, m => m.Libelle == "CONTINUES" && m.Valeur is null);

        var deck = f.Joueurs[1];
        Assert.Equal("PLAYER DECK", deck.Pseudo);
        Assert.Equal("GREEN PLATE", deck.Personnage);
        Assert.Equal("", deck.Pastille);
        Assert.Equal(3, deck.Mesures.Count);
        Assert.Equal("268", deck.Mesures[0].Texte);
    }

    [Fact]
    public void Le_verdict_rend_le_rang_certifie_et_rejoue_le_titre()
    {
        var provisoire = WorldScoringEcrans.ComposerLeBilan(Bilan(), Anglais);
        var certifie = WorldScoringEcrans.ComposerLeBilan(Bilan("published", 4), Anglais);

        Assert.NotEqual(provisoire.Cle, certifie.Cle);
        Assert.Equal("CERTIFIED", certifie.MonScore!.Second);
        Assert.Equal("Certified: rank #4.", certifie.Note);
        Assert.Contains(certifie.Joueurs[0].Mesures, m => m.Libelle == "CONTINUES" && m.Valeur == 0 && m.Meilleure);

        var record = WorldScoringEcrans.ComposerLeBilan(Bilan("published", 1) with { Score = 3_100_000 }, Anglais);
        Assert.Equal("NEW RECORD!", record.Titre);
        Assert.True(record.MonScore!.Premier);
        Assert.True(record.Progression!.Atteint);
        Assert.Equal(1, record.Progression.Part);
    }

    [Fact]
    public void Un_score_sans_record_ni_classement_ou_non_classe_dit_la_verite()
    {
        var nonCertifiable = WorldScoringEcrans.ComposerLeBilan(Bilan() with { Prevol = "scoring_uncertified" }, Anglais);
        Assert.Equal("NOT RANKED", nonCertifiable.Titre);
        // Aucun rang n'est promis a un score qui ne sera pas classe.
        Assert.Equal("1CC", nonCertifiable.MonScore!.Ruban);
        Assert.Equal("", nonCertifiable.MonScore.Second);
        Assert.Null(nonCertifiable.Joueurs[0].Rang);
        Assert.Equal("", nonCertifiable.Progression!.RangGauche);
        Assert.Equal("NOT RANKED", WorldScoringEcrans.ComposerLeBilan(Bilan("rejected"), Anglais).Titre);
        Assert.Equal("ON HOLD", WorldScoringEcrans.ComposerLeBilan(Bilan("held") with { Score = 900_000 }, Anglais).Titre);
        Assert.Equal("GAME OVER", WorldScoringEcrans.ComposerLeBilan(Bilan() with { Score = 900_000 }, Anglais).Titre);
        var rien = WorldScoringEcrans.ComposerLeBilan(Bilan() with { Score = null }, Anglais);
        Assert.Equal("NOT RANKED", rien.Titre);
        Assert.Null(rien.Progression);
        Assert.Equal("--", rien.MonScore!.Valeur);

        // Classement vide : le score classe est le sommet, la barre est pleine.
        var seul = WorldScoringEcrans.ComposerLeBilan(Bilan() with { RecordAvant = null, Sommet = null, SommetRang = 1, RangProvisoire = 1, RangAuDessus = null }, Anglais);
        Assert.True(seul.Progression!.Atteint);
        Assert.DoesNotContain(seul.Joueurs[0].Mesures, m => m.Libelle.StartsWith("TO ", StringComparison.Ordinal));
    }

    [Fact]
    public void Sans_deck_une_seule_carte()
    {
        var f = WorldScoringEcrans.ComposerLeBilan(Bilan() with { Deck = [] }, Anglais);
        Assert.Single(f.Joueurs);
    }

    // ── Le service : ce que le bilan tire de la partie ──────────────────

    private static LeaderboardClient.Ligne Ligne(int rang, string joueur, long valeur, bool moi = false)
        => new(rang, joueur, valeur, "", "FR", "", false, null, "", moi);

    private static readonly IReadOnlyList<LeaderboardClient.Ligne> Monde =
    [
        Ligne(1, "ZERO", 2_950_000), Ligne(2, "MOMO", 1_500_000), Ligne(3, "AKIRA", 1_310_900), Ligne(4, "RAFALE", 1_200_000),
        Ligne(5, "NELFE", 1_104_200, moi: true), Ligne(6, "KIM", 900_000),
    ];

    [Fact]
    public void Le_bilan_classe_le_score_dans_le_classement_du_depart()
    {
        var b = WorldScoringEcransService.BilanDe(Depart(), Monde, 1_286_500, 754_000, null, "scoring_certifiable", "", [], null);

        Assert.Equal(4, b.RangProvisoire);
        Assert.Equal(3, b.RangAuDessus);
        Assert.Equal(24_400, b.EcartAuDessus);
        Assert.Equal(2_950_000, b.Sommet);
        Assert.Equal(1, b.SommetRang);
        Assert.Equal(6, b.Classes);
        Assert.Equal(754_000, b.DureeMs);
        Assert.Equal("", b.Verdict);
        Assert.Null(b.RangCertifie);
    }

    [Fact]
    public void Le_verdict_l_emporte_sur_ce_qui_a_ete_lu()
    {
        var b = WorldScoringEcransService.BilanDe(Depart(), Monde, 1_000_000, 754_000, ("published", 4, 1_286_500, 0, 760_000), "scoring_certifiable", "", [], 1);

        Assert.Equal(1_286_500, b.Score);
        Assert.Equal(4, b.RangCertifie);
        Assert.Equal(0, b.Continues);
        Assert.Equal(760_000, b.DureeMs);
        Assert.Equal(1, b.ViesPerdues);
    }

    [Fact]
    public void Sous_son_record_l_ecart_ne_vise_jamais_sa_propre_ligne()
    {
        var b = WorldScoringEcransService.BilanDe(Depart(), Monde, 1_000_000, null, null, "", "", [], null);

        Assert.Equal(6, b.RangProvisoire);
        // NELFE (sa propre ligne, 1 104 200) est au-dessus : la cible est la ligne d'un autre, RAFALE.
        Assert.Equal(4, b.RangAuDessus);
        Assert.Equal(200_000, b.EcartAuDessus);
    }

    [Fact]
    public void La_regle_du_verdict_se_reconnait()
    {
        Assert.True(WorldScoringEcransService.MemeRegle("1cc", "1cc"));
        Assert.True(WorldScoringEcransService.MemeRegle("ket:1cc", "1cc"));
        Assert.False(WorldScoringEcransService.MemeRegle("1lc", "1cc"));
        Assert.False(WorldScoringEcransService.MemeRegle("", "1cc"));
    }

    [Fact]
    public void Les_stats_du_deck_se_lisent_bornees()
    {
        var json = System.Text.Json.JsonDocument.Parse("""
            {"LaunchId":"l-9","DeckId":"ketsui-green-plate","DeckName":"Green Plate","LivesLost":1,"PlayMs":754000,
             "Rows":[{"Label":"MAX CHIPS","Value":"268"},{"Label":"","Value":"x"},{"Label":"BOMBS USED","Value":"2"}]}
            """).RootElement;
        var r = RetroBat.Api.Deck.DeckRecap.Lire(json, null);

        Assert.NotNull(r);
        Assert.Equal("l-9", r!.LaunchId);
        Assert.Equal("Green Plate", r.DeckName);
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal(1, r.LivesLost);
        Assert.Equal(-1, RetroBat.Api.Deck.DeckRecap.Lire(System.Text.Json.JsonDocument.Parse("{}").RootElement, "x")!.LivesLost);
    }

    // ── Les apercus ──────────────────────────────────────────────────────

    private static readonly string? Sortie = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS");
    private static readonly string? Images = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS_IMAGES");
    private static readonly string? Medias = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS_MEDIAS");
    private static readonly string? Film = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS_FILM");

    private static string DossierDesPolices()
    {
        // Le dossier resources/ n'est pas versionne : un worktree le prend a l'installation (NELFEPLAY_APERCUS_POLICES).
        if (Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS_POLICES") is { Length: > 0 } donne && Directory.Exists(donne)) return donne;
        for (var dossier = new DirectoryInfo(AppContext.BaseDirectory); dossier is not null; dossier = dossier.Parent)
        {
            var candidat = Path.Combine(dossier.FullName, "resources", "theme", "fonts");
            if (Directory.Exists(candidat)) return candidat;
        }

        return Path.Combine(AppContext.BaseDirectory, "resources", "theme", "fonts");
    }

    [Fact]
    public void Les_ecrans_du_world_scoring_se_dessinent_a_cote_de_ceux_du_multiplayer()
    {
        if (Sortie is null || Images is null || Medias is null)
        {
            return;   // outil de mise au point, hors des passes ordinaires
        }

        Directory.CreateDirectory(Sortie);
        PolicesDesEcrans.Dossier = DossierDesPolices();
        var style = EsMenuStyle.Lire(null);
        string Fanart(string systeme, string jeu) => Path.Combine(Medias, "systems", systeme, "games", jeu, "artwork", "fanart.jpg");
        string Image(string relatif) => Path.Combine(Images, relatif);
        void Ecrire(string nom, byte[] png) => File.WriteAllBytes(Path.Combine(Sortie, nom), png);
        var socle = new L.ContenuMultiplayer([""], 0, false, "", "", "", null, [], -1, "", "", [], PleinEcran: true);
        string Nom(string prefixe, double t) => $"{prefixe}-{t:0.00}s.png".Replace(',', '.');

        // Le World Scoring : Ketsui en 1CC (fanart du jeu), au depart puis au bilan, provisoire puis certifie.
        var depart = Depart() with { Fanart = Fanart("arcade", "ket") };
        var chargement = WorldScoringEcrans.ComposerLeDepart(depart, Anglais, false, null);
        var pret = WorldScoringEcrans.ComposerLeDepart(depart, Anglais, true, DateTime.UtcNow);
        foreach (var t in new[] { 0.1, 0.3, 0.6, 2.0 })
            Ecrire(Nom("ws-depart", t), L.ApercuMultiplayer(style, socle with { Lancement = chargement }, "", "", "", null!, t));
        foreach (var s in new[] { 0.1, 0.3, 0.6 })
            Ecrire(Nom("ws-depart-go", s), L.ApercuMultiplayer(style, socle with { Lancement = pret }, "", "", "", null!, 3, s));
        Ecrire("ws-depart-verification.png", L.ApercuMultiplayer(style, socle with
        {
            Lancement = WorldScoringEcrans.ComposerLeDepart(depart with { Prevol = "" }, Anglais, false, null),
        }, "", "", "", null!, 2));
        Ecrire("ws-depart-record-perso.png", L.ApercuMultiplayer(style, socle with
        {
            Lancement = WorldScoringEcrans.ComposerLeDepart(depart with { Record = 3_000_000, MonRang = 1, Cible = new CibleDuScore(1, "NELFE", "FR", 3_000_000, true) },
                Anglais, false, null),
        }, "", "", "", null!, 2));
        Ecrire("ws-depart-premier-score.png", L.ApercuMultiplayer(style, socle with
        {
            Lancement = WorldScoringEcrans.ComposerLeDepart(depart with { Record = null, MonRang = null, Classes = 0, Cible = null }, Anglais, false, null),
        }, "", "", "", null!, 2));

        var bilan = Bilan() with { Fanart = Fanart("arcade", "ket") };
        var provisoire = WorldScoringEcrans.ComposerLeBilan(bilan, Anglais);
        foreach (var t in new[] { 0.3, 0.9, 1.6, 2.85, 6.0 })
            Ecrire(Nom("ws-bilan", t), L.ApercuMultiplayer(style, socle with { Fin = provisoire }, "", "", "", null!, t));
        Ecrire("ws-bilan-certifie.png", L.ApercuMultiplayer(style, socle with
        {
            Fin = WorldScoringEcrans.ComposerLeBilan(bilan with { Verdict = "published", RangCertifie = 4, Continues = 0 }, Anglais),
        }, "", "", "", null!, 6));
        Ecrire("ws-bilan-record.png", L.ApercuMultiplayer(style, socle with
        {
            Fin = WorldScoringEcrans.ComposerLeBilan(bilan with { Score = 3_100_000, Verdict = "published", RangCertifie = 1, RangProvisoire = 1, RangAuDessus = null, Continues = 0 },
                Anglais),
        }, "", "", "", null!, 6));
        Ecrire("ws-bilan-sans-deck.png", L.ApercuMultiplayer(style, socle with
        {
            Fin = WorldScoringEcrans.ComposerLeBilan(bilan with { Deck = [], ViesPerdues = null, Score = 900_000, RangProvisoire = 31, RangAuDessus = 30, EcartAuDessus = 2_300 },
                Anglais),
        }, "", "", "", null!, 6));
        Ecrire("ws-bilan-non-classe.png", L.ApercuMultiplayer(style, socle with
        {
            Fin = WorldScoringEcrans.ComposerLeBilan(bilan with { Prevol = "scoring_uncertified" }, Anglais),
        }, "", "", "", null!, 6));

        // Les references MULTIPLAYER, rendues par le meme code aux memes temps.
        var depuis = DateTime.UtcNow.AddSeconds(-5);
        var vs = new L.EcranDeLancement("l-1", "combat", "VS", "Street Fighter II' Champion Edition", "RENCONTRE CLASSÉE", "duel", Fanart("arcade", "sf2ce"),
        [
            new(1, "NELFE", "VEGA", Image("arcade/sf2ce/character/vega.png"), "FR", "A", "1 842", "VICTOIRES", "128", "SÉRIE", "5 VICTOIRES", E.Pret, "PRÊT !", 24, 4, depuis),
            new(2, "AKIRA", "GUILE", Image("arcade/sf2ce/character/guile.png"), "JP", "A", "1 796", "VICTOIRES", "96", "SÉRIE", "3 VICTOIRES", E.Synchronisation,
                "SYNCHRONISATION…", 31, 3, depuis),
        ], "2 MANCHES GAGNANTES", "LE CONSEIL DU COIN", "Contre un saut, on garde debout : la garde basse ne l'arrête pas.", "On attend AKIRA…",
            ["JOUEURS RÉUNIS", "SYNCHRONISATION", "C'EST PARTI !"], 1, "À VOUS DE JOUER !", null, false, "RÉESSAYER", "QUITTER", "MAINTENIR", "button_a", "button_b");
        foreach (var t in new[] { 0.1, 0.3, 0.6, 2.0 })
            Ecrire(Nom("mp-vs", t), L.ApercuMultiplayer(style, socle with { Lancement = vs }, "", "", "", null!, t));
        Ecrire(Nom("mp-vs-go", 0.3), L.ApercuMultiplayer(style, socle with { Lancement = vs with { DepartUtc = DateTime.UtcNow, Etape = 2 } }, "", "", "", null!, 3, 0.3));

        var textes = new L.TextesDeFin("FIN DE PARTIE", "VAINQUEUR", "GG ! La revanche est à un bouton.", "MAINTENIR", "REVANCHE", "QUITTER");
        var fin = new L.EcranDeFin("salle-1", "combat", "VICTOIRE !", "Street Fighter II' Champion Edition", "1:01", Fanart("arcade", "sf2ce"),
        [
            new(1, "NELFE", "VEGA", Image("arcade/sf2ce/character/vega.png"), "A", "1 858", "+16", true, 1, "",
                [new("gant", "COUPS", 14, 1, true), new("eclair", "DÉGÂTS", 288, 1, true), new("diamant", "PERFECTS", 0, 0, false)]),
            new(2, "AKIRA", "GUILE", Image("arcade/sf2ce/character/guile.png"), "A", "1 780", "-16", false, 2, "INTOUCHABLE",
                [new("gant", "COUPS", 9, 9 / 14.0, false), new("eclair", "DÉGÂTS", 190, 190 / 288.0, false), new("diamant", "PERFECTS", 1, 1, true)]),
        ], new L.JaugeDeFin("DÉGÂTS INFLIGÉS", [new(1, 0.6), new(2, 0.4)]), "", "", textes, "button_a", "button_b");
        foreach (var t in new[] { 0.3, 0.9, 1.6, 2.85, 6.0 })
            Ecrire(Nom("mp-fin", t), L.ApercuMultiplayer(style, socle with { Fin = fin }, "", "", "", null!, t));

        if (Film is null) return;

        // La suite d'images de l'animation, a 30 images/s : le depart (entree 2,2 s, puis YOUR TURN! 1,3 s) et le bilan (3,5 s).
        var dossier = Path.Combine(Sortie, "film");
        Directory.CreateDirectory(dossier);
        var n = 0;
        for (var i = 0; i < 66; i++)
            File.WriteAllBytes(Path.Combine(dossier, $"f{n++:0000}.png"), L.ApercuMultiplayer(style, socle with { Lancement = chargement }, "", "", "", null!, i / 30.0));
        for (var i = 0; i < 40; i++)
            File.WriteAllBytes(Path.Combine(dossier, $"f{n++:0000}.png"), L.ApercuMultiplayer(style, socle with { Lancement = pret }, "", "", "", null!, 3, i / 30.0));
        for (var i = 0; i < 105; i++)
            File.WriteAllBytes(Path.Combine(dossier, $"f{n++:0000}.png"), L.ApercuMultiplayer(style, socle with { Fin = provisoire }, "", "", "", null!, i / 30.0));
    }
}
