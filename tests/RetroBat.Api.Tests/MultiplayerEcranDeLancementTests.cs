using System.Runtime.Versioning;
using RetroBat.Api.Leaderboard;
using Xunit;
using E = RetroBat.Api.Leaderboard.LeaderboardOverlayService.EtatAuLancement;
using L = RetroBat.Api.Leaderboard.LeaderboardOverlayService;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'ECRAN DE LANCEMENT MULTIPLAYER, le VS (maquettes du 28/10) : le mot coupe en deux lignes, et les apercus des quatre
/// declinaisons (combat, puzzle, course a quatre, coop a trois) a plusieurs temps de l'animation. Les apercus (PNG) ne se
/// dessinent que si NELFEPLAY_APERCUS donne un dossier de sortie.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(PolicesPartagees.Nom)]
public class MultiplayerEcranDeLancementTests
{
    [Theory]
    [InlineData("VS", new[] { "VS" })]
    [InlineData("EN PISTE !", new[] { "EN", "PISTE !" })]
    [InlineData("ON FAIT ÉQUIPE !", new[] { "ON FAIT", "ÉQUIPE !" })]
    [InlineData("TEAMWORK!", new[] { "TEAMWORK!" })]
    public void Le_mot_se_coupe_a_l_espace_le_plus_proche_du_milieu(string mot, string[] attendu)
        => Assert.Equal(attendu, L.MotEnDeuxLignes(mot));

    [Theory]
    [InlineData("combat", "duel", "multiplayer.launch.word.versus")]
    [InlineData("puzzle", "duel", "multiplayer.launch.word.versus")]
    [InlineData("course", "course", "multiplayer.launch.word.race")]
    [InlineData("coop", "coop", "multiplayer.launch.word.coop")]
    [InlineData("combat", "coop", "multiplayer.launch.word.coop")]
    public void Le_mot_central_suit_le_type_et_la_famille(string type, string famille, string attendu)
        => Assert.Equal(attendu, RetroBat.Api.Multiplayer.MultiplayerPanelService.CleDuMot(type, famille));

    [Theory]
    [InlineData("fr", "Conseil")]
    [InlineData("fr_FR", "Conseil")]
    [InlineData("de", "Tip")]
    public void Le_conseil_se_prend_dans_la_langue_puis_en_anglais(string langue, string attendu)
        => Assert.Equal(attendu, RetroBat.Api.Multiplayer.MultiplayerPanelService.ConseilDans(new Dictionary<string, string> { ["fr"] = "Conseil", ["en"] = "Tip" }, langue));

    [Fact]
    public void Sans_conseil_rien_ne_s_affiche()
        => Assert.Equal(string.Empty, RetroBat.Api.Multiplayer.MultiplayerPanelService.ConseilDans(null, "fr"));

    private static readonly string? Sortie = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS");
    private static readonly string? Images = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS_IMAGES");
    private static readonly string? Medias = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS_MEDIAS");

    [Fact]
    public void L_ecran_de_lancement_se_dessine_dans_chaque_declinaison()
    {
        if (Sortie is null || Images is null || Medias is null)
        {
            return;   // outil de mise au point, hors des passes ordinaires
        }

        Directory.CreateDirectory(Sortie);
        PolicesDesEcrans.Dossier = DossierDesPolices();
        var style = EsMenuStyle.Lire(null);
        string Image(string relatif) => Path.Combine(Images, relatif);
        string Fanart(string systeme, string jeu) => Path.Combine(Medias, "systems", systeme, "games", jeu, "artwork", "fanart.jpg");
        var depuis = DateTime.UtcNow.AddSeconds(-5);
        L.JoueurDeLancement Joueur(int place, string pseudo, string perso, string? portrait, string pays, string lettre, string cote, string victoires, string serie,
            E etat, string texte, int? ping, int? barres)
            => new(place, pseudo, perso, portrait, pays, lettre, cote, "VICTOIRES", victoires, "SÉRIE", serie, etat, texte, ping, barres, depuis);

        var etapes = new[] { "JOUEURS RÉUNIS", "SYNCHRONISATION", "C'EST PARTI !" };
        var combat = new L.EcranDeLancement("l-1", "combat", "VS", "Street Fighter II' Champion Edition", "RENCONTRE CLASSÉE", "duel", Fanart("arcade", "sf2ce"),
        [
            Joueur(1, "NELFE", "VEGA", Image("arcade/sf2ce/character/vega.png"), "FR", "A", "1 842", "128", "5 VICTOIRES", E.Pret, "PRÊT !", 24, 4),
            Joueur(2, "AKIRA", "GUILE", Image("arcade/sf2ce/character/guile.png"), "JP", "A", "1 796", "96", "3 VICTOIRES", E.Synchronisation, "SYNCHRONISATION…", 31, 3),
        ], "2 MANCHES GAGNANTES", "LE CONSEIL DU COIN", "Contre un saut, on garde debout : la garde basse ne l'arrête pas.", "On attend AKIRA…", etapes, 1,
            "À VOUS DE JOUER !", null, false, "RÉESSAYER", "QUITTER", "MAINTENIR", "button_a", "button_b");
        var puzzle = combat with
        {
            Cle = "l-2", Type = "puzzle", Jeu = "Magical Drop III", Fanart = Fanart("arcade", "magdrop3"), Conseil = "Une chaîne en déclenche une autre : c'est elle qui remplit le puits d'en face.",
            Joueurs =
            [
                Joueur(1, "NELFE", "FOOL", Image("arcade/magdrop3/character/fool.png"), "FR", "B", "1 544", "42", "2 VICTOIRES", E.Pret, "PRÊT !", 19, 4),
                Joueur(2, "AKIRA", "EMPRESS", Image("arcade/magdrop3/character/empress.png"), "JP", "B", "1 490", "37", "1 VICTOIRE", E.Chargement, "CHARGEMENT…", 88, 2),
            ],
        };
        var machines = Image("gba/f-zero-maximum-velocity/machine");
        var course = combat with
        {
            Cle = "l-3", Type = "course", Mot = "EN PISTE !", Jeu = "F-Zero: Maximum Velocity", SousTitre = "course à 4", Fanart = Fanart("gba", "f_zero_maximum_velocity"),
            Etiquette = "", Conseil = "Le boost arrive au deuxième tour : gardez-le pour les lignes droites.", Legende = "On attend RAFALE et MOMO…",
            Joueurs =
            [
                Joueur(1, "NELFE", "HOT VIOLET", Path.Combine(machines, "hot-violet.png"), "FR", "B", "1 508", "", "", E.Pret, "PRÊT !", 24, 4),
                Joueur(2, "AKIRA", "FIRE BALL", Path.Combine(machines, "fire-ball.png"), "JP", "B", "1 460", "", "", E.Pret, "PRÊT !", 31, 3),
                Joueur(3, "RAFALE", "J.B. CRYSTAL", Path.Combine(machines, "j-b-crystal.png"), "BE", "B", "1 532", "", "", E.Synchronisation, "SYNCHRONISATION…", 46, 3),
                Joueur(4, "MOMO", "WIND WALKER", Path.Combine(machines, "wind-walker.png"), "IT", "B", "1 495", "", "", E.Chargement, "CHARGEMENT…", 112, 2),
            ],
        };
        var coop = combat with
        {
            Cle = "l-4", Type = "coop", Mot = "ON FAIT ÉQUIPE !", Jeu = "X-Men", Entete = "PARTIE EN COOP", SousTitre = "3 joueurs", Fanart = Fanart("arcade", "xmen"),
            Etiquette = "", Conseil = "Frappez à plusieurs le même ennemi : il tombe bien plus vite.", Legende = "On attend MOMO…",
            Joueurs =
            [
                Joueur(1, "NELFE", "WOLVERINE", Image("arcade/xmen/character/wolverine.png"), "FR", "", "", "", "", E.Pret, "PRÊT !", 24, 4),
                Joueur(2, "AKIRA", "CYCLOPS", Image("arcade/xmen/character/cyclops.png"), "JP", "", "", "", "", E.Pret, "PRÊT !", 31, 3),
                Joueur(3, "MOMO", "STORM", Image("arcade/xmen/character/storm.png"), "IT", "", "", "", "", E.Synchronisation, "SYNCHRONISATION…", 46, 3),
            ],
        };

        var socle = new L.ContenuMultiplayer([""], 0, false, "", "", "", null, [], -1, "", "", [], PleinEcran: true);
        foreach (var temps in new[] { 0.1, 0.3, 0.6, 2.0 })
        {
            Ecrire($"lancement-combat-{temps:0.0}s.png", L.ApercuMultiplayer(style, socle with { Lancement = combat }, "", "", "", null!, temps));
        }

        Ecrire("lancement-puzzle.png", L.ApercuMultiplayer(style, socle with { Lancement = puzzle }, "", "", "", null!, 2));
        Ecrire("lancement-course.png", L.ApercuMultiplayer(style, socle with { Lancement = course }, "", "", "", null!, 2));
        Ecrire("lancement-coop.png", L.ApercuMultiplayer(style, socle with { Lancement = coop }, "", "", "", null!, 2));
        // Plusieurs personnages par place (option B du user, 30/10) : le premier en grand, les suivants en vignettes.
        var mvc = combat with
        {
            Cle = "l-8", Jeu = "Marvel vs. Capcom", Fanart = Fanart("arcade", "mvsc"),
            Joueurs =
            [
                combat.Joueurs[0] with { Personnage = "RYU / MEGA MAN", Portrait = Image("arcade/mvsc/character/ryu.png"),
                    Coequipiers = [Image("arcade/mvsc/character/mega-man.png")] },
                combat.Joueurs[1] with { Personnage = "WOLVERINE / HULK", Portrait = Image("arcade/mvsc/character/wolverine.png"),
                    Coequipiers = [Image("arcade/mvsc/character/hulk.png")] },
            ],
        };
        Ecrire("lancement-equipes.png", L.ApercuMultiplayer(style, socle with { Lancement = mvc }, "", "", "", null!, 2));
        Ecrire("lancement-equipes-trois.png", L.ApercuMultiplayer(style, socle with
        {
            Lancement = mvc with
            {
                Cle = "l-9",
                Joueurs = [mvc.Joueurs[0] with { Coequipiers = [Image("arcade/mvsc/character/mega-man.png"), Image("arcade/mvsc/character/chun-li.png")] },
                    mvc.Joueurs[1] with { Coequipiers = [Image("arcade/mvsc/character/hulk.png"), Image("arcade/mvsc/character/venom.png")] }],
            },
        }, "", "", "", null!, 2));
        Ecrire("lancement-sans-portrait.png", L.ApercuMultiplayer(style, socle with
        {
            Lancement = combat with { Cle = "l-5", Joueurs = [.. combat.Joueurs.Select(j => j with { Portrait = null, Personnage = "" })] },
        }, "", "", "", null!, 2));
        Ecrire("lancement-attente-longue.png", L.ApercuMultiplayer(style, socle with
        {
            Lancement = combat with { Cle = "l-6", AttenteLongue = true, Joueurs = [combat.Joueurs[0], combat.Joueurs[1] with { Etat = E.EnRoute, TexteDeLEtat = "EN ROUTE DEPUIS 1 MIN" }] },
        }, "", "", "", null!, 2));
        Ecrire("lancement-depart.png", L.ApercuMultiplayer(style, socle with
        {
            Lancement = combat with { Cle = "l-7", Etape = 2, Legende = "Tout le monde est prêt", DepartUtc = DateTime.UtcNow,
                Joueurs = [combat.Joueurs[0], combat.Joueurs[1] with { Etat = E.Pret, TexteDeLEtat = "PRÊT !" }] },
        }, "", "", "", null!, 0.6));
    }

    private static void Ecrire(string nom, byte[] png)
    {
        Assert.True(png.Length > 0);
        File.WriteAllBytes(Path.Combine(Sortie!, nom), png);
    }

    private static string DossierDesPolices()
    {
        for (var dossier = new DirectoryInfo(AppContext.BaseDirectory); dossier is not null; dossier = dossier.Parent)
        {
            var candidat = Path.Combine(dossier.FullName, "resources", "theme", "fonts");
            if (Directory.Exists(candidat))
            {
                return candidat;
            }
        }

        throw new DirectoryNotFoundException("resources/theme/fonts introuvable");
    }
}
