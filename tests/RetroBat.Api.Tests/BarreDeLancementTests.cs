using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.MassRun;
using RetroBat.Api.Multiplayer;
using RetroBat.Api.Speedrun;
using Xunit;
using E = RetroBat.Api.Leaderboard.LeaderboardOverlayService.EtatAuLancement;
using L = RetroBat.Api.Leaderboard.LeaderboardOverlayService;
using P = RetroBat.Api.Leaderboard.ProgressionDuLancement;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA BARRE DISCRETE DU BAS DES ECRANS DE LANCEMENT (13/11) : un seul modele (<see cref="ProgressionDuLancement"/>) et un seul
/// dessin (<see cref="BarreDeLancement"/>) pour le speedrun, le challenge, le World Scoring, le MULTIPLAYER, le MASS RUN et la
/// boite d'attente. La progression est honnete (une etape finie, un cran), le texte reste en anglais. Les apercus (PNG) ne se
/// dessinent que si NELFEPLAY_APERCUS donne un dossier de sortie.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(PolicesPartagees.Nom)]
public class BarreDeLancementTests
{
    private static readonly string Modele = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Reglements", "sonic-the-hedgehog.green-hill-1.json"));
    private static readonly Reglement GreenHill1 = Reglement.Lire(Modele);
    private static readonly string? Sortie = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS");
    private static readonly DateTime Maintenant = new(2026, 11, 13, 20, 0, 0, DateTimeKind.Utc);

    private static P Trois() => new("essai", [("a", "Doing A…"), ("b", "Doing B…"), ("c", "Doing C…")]);

    // ── Le modele ─────────────────────────────────────────────────────────

    [Fact]
    public void La_barre_n_avance_que_quand_une_etape_finit_vraiment()
    {
        var p = Trois();
        Assert.Equal(0, p.Lire().Part);
        p.Commencer("a");
        Assert.Equal(0, p.Lire().Part);
        Assert.Equal("Doing A…", p.Lire().Texte);
        p.Finir("a");
        Assert.Equal(1 / 3d, p.Lire().Part, 6);
        // entre deux etapes, la prochaine se dit
        Assert.Equal("Doing B…", p.Lire().Texte);
        p.Finir("b");
        p.Finir("c");
        Assert.True(p.Lire().Finie);
        Assert.Equal(P.TexteFini, p.Lire().Texte);
    }

    [Fact]
    public void Des_etapes_paralleles_se_disent_dans_l_ordre_et_avancent_chacune_a_sa_fin()
    {
        var p = Trois();
        p.Commencer("a");
        p.Commencer("b");
        p.Commencer("c");
        p.Finir("c");
        Assert.Equal("Doing A…", p.Lire().Texte);
        Assert.Equal(1, p.Lire().Faites);
        p.Finir("a");
        Assert.Equal("Doing B…", p.Lire().Texte);
        Assert.Equal(2, p.Lire().Faites);
    }

    [Fact]
    public void Une_etape_qui_n_aura_pas_lieu_sort_du_compte_et_la_part_ne_recule_jamais()
    {
        var p = Trois();
        p.Finir("a");
        var avant = p.Lire().Part;
        p.Retirer("b");
        Assert.Equal(2, p.Lire().Total);
        Assert.True(p.Lire().Part > avant);
        // une etape commencee ou finie ne se retire pas
        p.Commencer("c");
        p.Retirer("c");
        p.Retirer("a");
        Assert.Equal(2, p.Lire().Total);
    }

    [Fact]
    public void Un_echec_compte_comme_passe_et_se_voit()
    {
        var p = Trois();
        Assert.Throws<InvalidOperationException>(() => p.Mesurer<int>("a", () => throw new InvalidOperationException("lanceur")));
        Assert.True(p.Lire().Echec);
        Assert.Equal(1, p.Lire().Faites);
        // plus rien ne tourne : la barre dit l'echec, pas l'etape suivante
        Assert.Equal(P.TexteEchec, p.Lire().Texte);
        Assert.Equal(P.EtatDEtape.Echouee, p.Etapes()[0].Etat);
    }

    [Fact]
    public async Task Mesurer_rend_le_resultat_et_marque_l_etape()
    {
        var p = Trois();
        Assert.Equal(7, p.Mesurer("a", () => 7));
        Assert.True(await p.MesurerAsync("b", async () => { await Task.Delay(20); return true; }));
        await p.MesurerAsync("c", () => Task.Delay(20));
        Assert.True(p.Lire().Finie);
        Assert.All(p.Etapes(), e => Assert.NotNull(e.DureeMs));
        Assert.True(p.Etapes()[1].DureeMs >= 15);
    }

    [Fact]
    public void Une_etape_inconnue_ou_deja_finie_ne_change_rien()
    {
        var p = Trois();
        p.Finir("inconnue");
        p.Echouer("inconnue");
        p.Commencer("inconnue");
        Assert.Equal(0, p.Lire().Faites);
        p.Finir("a");
        p.Echouer("a");
        Assert.False(p.Lire().Echec);
        Assert.Equal(1, p.Lire().Faites);
    }

    [Fact]
    public void Des_etapes_finies_de_plusieurs_fils_a_la_fois_se_comptent_toutes()
    {
        var p = new P("fils", Enumerable.Range(0, 64).Select(i => ("e" + i, "Step " + i + "…")));
        Parallel.For(0, 64, i =>
        {
            p.Commencer("e" + i);
            _ = p.Lire();
            p.Finir("e" + i);
        });
        Assert.True(p.Lire().Finie);
        Assert.Equal(64, p.Lire().Faites);
    }

    [Fact]
    public void Le_resume_du_journal_dit_chaque_etape()
    {
        var p = Trois();
        p.Commencer("a");
        p.Finir("a");
        p.Commencer("b");
        var resume = p.Resume();
        Assert.StartsWith("a ", resume);
        Assert.Contains("ms", resume);
        Assert.Contains("b en cours", resume);
        Assert.Contains("c a venir", resume);
    }

    [Fact]
    public void Les_textes_restent_en_anglais_quelle_que_soit_la_langue()
    {
        var toutes = new[]
        {
            P.Epreuve("e", false), P.Epreuve("e", true), P.WorldScoring("w"), P.MassRun("m"), P.Multiplayer("h", true), P.Multiplayer("g", false),
            P.RejoindreUnDirect("d"), P.RegarderUnReplay("r"),
        };
        foreach (var p in toutes)
        {
            Assert.All(p.Etapes(), e => Assert.Matches(new Regex("^[A-Z][A-Za-z' ]+…$"), e.Texte));
        }
    }

    [Fact]
    public void Chaque_ecran_a_ses_etapes_reelles()
    {
        Assert.Equal([P.Regles, P.Fantomes, P.Deck, P.Manettes, P.Lanceur, P.PremiereImage], P.Epreuve("e", false).Ids);
        Assert.Equal("Loading the start state…", P.Epreuve("e", true).Etapes()[^1].Texte);
        // un defi prive qui part par ES : ni direct, ni manettes calculees par l'API
        var ws = P.WorldScoring("w");
        ws.Retirer(P.Direct);
        ws.Retirer(P.Manettes);
        Assert.Equal([P.Menu, P.Coeur, P.Deck, P.Lanceur, P.Jeu], ws.Ids);
        Assert.Equal([P.Place, P.Course, P.Manettes, P.Lanceur, P.SalleDAttente], P.MassRun("m").Ids);
        Assert.Equal(P.Joueurs, P.Multiplayer("h", true).Ids[^1]);
        Assert.Equal(P.Session, P.Multiplayer("g", false).Ids[0]);
        Assert.Equal(P.Hote, P.Multiplayer("g", false).Ids[^1]);
    }

    [Theory]
    [InlineData(0.0, 0.5, 0.0)]
    [InlineData(0.0, 0.5, 0.066)]
    [InlineData(0.2, 0.8, 0.3)]
    [InlineData(0.5, 0.5, 0.066)]
    public void La_part_montree_rejoint_la_vraie_sans_jamais_la_depasser(double montree, double vraie, double secondes)
    {
        var suivante = BarreDeLancement.Lisser(montree, vraie, secondes);
        Assert.InRange(suivante, Math.Min(montree, vraie), vraie);
        var x = montree;
        for (var i = 0; i < 60; i++) x = BarreDeLancement.Lisser(x, vraie, 0.066);
        Assert.Equal(vraie, x);
    }

    // ── Les ecrans portent la meme progression ───────────────────────────────

    [Fact]
    public void Chaque_ecran_de_lancement_porte_la_progression_qu_on_lui_donne()
    {
        var p = P.Epreuve("e", false);
        var service = new DefisPanelService(null!, null!, null!, null!, null!);
        Assert.Same(p, service.ComposerLaPreparation(GreenHill1, "Sonic the Hedgehog", null, false, "fr", progression: p).Progression);
        Assert.Null(service.ComposerLaPreparation(GreenHill1, "Sonic the Hedgehog", null, false, "fr").Progression);

        var depart = new DepartDuScore("ket-1", "Ketsui", null, "1CC", false, "NELFE", "FR", 1_104_200, 16, 212, null, "scoring_certifiable");
        var ws = P.WorldScoring("w");
        Assert.Same(ws, WorldScoringEcrans.ComposerLeDepart(depart, (_, d) => d, false, null, progression: ws).Progression);

        var m = P.MassRun("m");
        var salle = new MassRunService.SalleOuverte(4242, "nes/super-mario-bros", "1-1", "debut", Maintenant.AddSeconds(150), 180, 12, 0, Maintenant.AddSeconds(90));
        Assert.Same(m, service.ComposerLeDepartMassRun(salle, "Super Mario Bros.", null, false, 12, Maintenant, progression: m).Progression);
    }

    [Fact]
    public void Le_film_de_la_rencontre_n_emporte_pas_la_barre()
    {
        var vs = new L.EcranDeLancement("l-1", "combat", "VS", "Jeu", "", "", null, [], "", "", "", "", ["A", "B", "C"], 1, "GO", null, false, "", "", "", "a", "b",
            Progression: P.Multiplayer("h", true));
        var contenu = new L.ContenuMultiplayer([""], 0, false, "", "", "", null, [], -1, "", "", [], PleinEcran: true, Lancement: vs);
        Assert.Null(MultiplayerCartesDuFilm.PourLeFilm(contenu).Lancement!.Progression);
    }

    // ── Les apercus ──────────────────────────────────────────────────────

    [Fact]
    public void La_barre_se_dessine_sur_chaque_ecran_de_lancement()
    {
        if (Sortie is null) return;   // outil de mise au point, hors des passes ordinaires

        Directory.CreateDirectory(Sortie);
        PolicesDesEcrans.Dossier = DossierDesPolices();
        var medias = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS_MEDIAS") ?? @"E:\RetroBat\plugins\APIExpose\media";
        string? Fanart(string systeme, string jeu) => Path.Combine(medias, "systems", systeme, "games", jeu, "artwork", "fanart.jpg") is var f && File.Exists(f) ? f : null;
        var style = EsMenuStyle.Lire(null);
        var socle = new L.ContenuMultiplayer([""], 0, false, "", "", "", null, [], -1, "", "", [], PleinEcran: true);
        void Ecrire(string nom, byte[] png) => File.WriteAllBytes(Path.Combine(Sortie!, nom), png);
        void Page(string nom, L.EcranDeLancement l, double t = 2, double? depart = null)
            => Ecrire(nom, L.ApercuMultiplayer(style, socle with { Lancement = l }, "", "", "", null!, t, depart));
        var service = new DefisPanelService(null!, null!, null!, null!, null!, textes: new RetroBat.Api.Infrastructure.InterfaceTextService()) { LangueDApercu = "fr" };

        // Speedrun : les controles passes, fantomes et manettes faits, le Deck rend encore son decor.
        var speedrun = P.Epreuve("epreuve-gh1", false);
        speedrun.Finir(P.Regles, P.Fantomes, P.Manettes);
        speedrun.Commencer(P.Deck);
        Page("01-speedrun-preparation.png", service.ComposerLaPreparation(GreenHill1, "Sonic the Hedgehog", Fanart("megadrive", "sonic_the_hedgehog"), false, "fr", progression: speedrun));
        // ... puis le jeu part : « A TOI ! », la barre s'en va.
        speedrun.Finir(P.Deck, P.Lanceur, P.PremiereImage);
        Page("02-speedrun-depart-sans-barre.png", service.ComposerLaPreparation(GreenHill1, "Sonic the Hedgehog", Fanart("megadrive", "sonic_the_hedgehog"), true, "fr",
            DateTime.UtcNow, speedrun), 3, 0.5);

        // Challenge : le meme ecran, le lanceur en cours.
        var challenge = P.Epreuve("epreuve-defi", false);
        challenge.Finir(P.Regles, P.Fantomes, P.Deck, P.Manettes);
        challenge.Commencer(P.Lanceur);
        var defi = Reglement.Lire(Modele.Replace("speedrun_level", "challenge_rule"));
        Page("03-challenge-preparation.png", service.ComposerLaPreparation(defi, "Sonic the Hedgehog", Fanart("megadrive", "sonic_the_hedgehog"), false, "fr", progression: challenge));

        // World Scoring : un defi prive avec le Deck, le decor en cours.
        var ws = P.WorldScoring("defi-ket");
        ws.Retirer(P.Direct);
        ws.Finir(P.Menu, P.Coeur);
        ws.Commencer(P.Deck);
        var depart = new DepartDuScore("ket-1", "Ketsui", Fanart("arcade", "ket"), "1CC", false, "NELFE", "FR", 1_104_200, 16, 212,
            new CibleDuScore(3, "AKIRA", "JP", 1_310_900, false), "scoring_certifiable");
        Page("04-world-scoring-depart.png", WorldScoringEcrans.ComposerLeDepart(depart, (_, d) => d, false, null, progression: ws));

        // MASS RUN : la place prise et la course preparee, les manettes en cours.
        var massrun = P.MassRun("massrun-4242");
        massrun.Finir(P.Place, P.Course);
        massrun.Commencer(P.Manettes);
        var salle = new MassRunService.SalleOuverte(4242, "nes/super-mario-bros", "1-1", "debut", Maintenant.AddSeconds(150), 180, 12, 0, Maintenant.AddSeconds(90));
        Page("05-massrun-depart.png", service.ComposerLeDepartMassRun(salle, "Super Mario Bros.", Fanart("nes", "super_mario_bros"), false, 12, Maintenant, progression: massrun));

        // MULTIPLAYER : l'hote a lance, sa session s'ouvre ; l'invite rejoint l'hote.
        var depuis = DateTime.UtcNow.AddSeconds(-5);
        L.JoueurDeLancement Joueur(int place, string pseudo, string pays, E etat, string texte, int ping)
            => new(place, pseudo, "", null, pays, "A", "1 842", "VICTOIRES", "12", "SÉRIE", "3", etat, texte, ping, 4, depuis);
        L.EcranDeLancement Vs(P progression) => new("mp-1", StylesDesEcrans.Coop, "ON FAIT ÉQUIPE !", "Metal Slug 3", "PARTIE COOP", "2 joueurs",
            Fanart("arcade", "mslug3"),
            [Joueur(1, "NELFE", "FR", E.Pret, "PRÊT !", 24), Joueur(2, "AKIRA", "JP", E.Synchronisation, "SYNCHRONISATION…", 31)],
            "", "", "", "On attend AKIRA…", ["JOUEURS RÉUNIS", "SYNCHRONISATION", "C'EST PARTI !"], 1, "À VOUS DE JOUER !", null, false,
            "RÉESSAYER", "QUITTER", "MAINTENIR", "a", "b", SortiePossible: true, Progression: progression);
        var hote = P.Multiplayer("mp-1", true);
        hote.Finir(P.Paquet, P.Relais, P.Manettes, P.Panneaux, P.Lanceur, P.Jeu);
        hote.Commencer(P.Session);
        Page("06-multiplayer-vs-hote.png", Vs(hote));
        var invite = P.Multiplayer("mp-1", false);
        invite.Finir(P.Session, P.Paquet, P.Manettes, P.Panneaux, P.Lanceur, P.Jeu);
        invite.Commencer(P.Hote);
        Page("07-multiplayer-vs-invite.png", Vs(invite));

        // Un lancement refuse : la barre passe au rouge (la page s'efface aussitot sur la borne).
        var refuse = P.Epreuve("epreuve-refus", false);
        refuse.Finir(P.Regles, P.Fantomes, P.Deck, P.Manettes);
        refuse.Echouer(P.Lanceur);
        Page("08-speedrun-lanceur-refuse.png", service.ComposerLaPreparation(GreenHill1, "Sonic the Hedgehog", Fanart("megadrive", "sonic_the_hedgehog"), false, "fr", progression: refuse));

        // La boite d'attente d'ES : le defi World Scoring sans ses ecrans (ES lance), le replay, le direct rejoint.
        var parEs = P.WorldScoring("defi-es");
        parEs.Retirer(P.Direct);
        parEs.Finir(P.Menu, P.Coeur, P.Deck);
        parEs.Retirer(P.Manettes);
        parEs.Commencer(P.Lanceur);
        Ecrire("09-boite-defi-par-es.png", L.ApercuMultiplayer(style, socle, "LANCEMENT DE LA PARTIE", "DÉFI", "BATS AKIRA : 1 310 900", null!, 2, null, parEs));
        var replay = P.RegarderUnReplay("replay-1");
        replay.Finir(P.Spectateur);
        replay.Commencer(P.Replay);
        Ecrire("10-boite-replay.png", L.ApercuMultiplayer(style, socle, "LANCEMENT", "", "", null!, 2, null, replay));
        var direct = P.RejoindreUnDirect("direct-1");
        direct.Finir(P.Menu);
        direct.Commencer(P.Session);
        Ecrire("11-boite-direct.png", L.ApercuMultiplayer(style, socle, "LANCEMENT DE LA PARTIE", "", "", null!, 2, null, direct));
    }

    private static string DossierDesPolices()
    {
        for (var dossier = new DirectoryInfo(AppContext.BaseDirectory); dossier is not null; dossier = dossier.Parent)
        {
            var candidat = Path.Combine(dossier.FullName, "resources", "theme", "fonts");
            if (Directory.Exists(candidat)) return candidat;
        }

        const string installees = @"E:/RetroBat/plugins/APIExpose/resources/theme/fonts";
        if (Directory.Exists(installees)) return installees;
        throw new DirectoryNotFoundException("resources/theme/fonts introuvable");
    }
}
