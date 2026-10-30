using System.Drawing;
using System.Runtime.Versioning;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.Multiplayer;
using Xunit;
using L = RetroBat.Api.Leaderboard.LeaderboardOverlayService;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'ECRAN DE FIN MULTIPLAYER v2 (CDC Ecrans v1.0, lots E1 et E2) : le calcul des etoiles, des distinctions et de la jauge,
/// la couleur du titre tiree du fanart, les types, les formes, les icones et les polices livrees par le Data Pack.
/// Les apercus (PNG) ne se dessinent que si NELFEPLAY_APERCUS donne un dossier de sortie.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(PolicesPartagees.Nom)]
public class MultiplayerEcranDeFinTests
{
    private static readonly MultiplayerStatsMesure Coups = new() { Category = "execution", Id = "coups", Label = "multiplayer.stat.hits" };
    private static readonly MultiplayerStatsMesure Degats = new() { Category = "efficacite", Id = "degats", Label = "multiplayer.stat.damage" };
    private static readonly MultiplayerStatsMesure Perfects = new() { Category = "maitrise", Id = "perfects", Label = "multiplayer.stat.perfects" };
    private static readonly MultiplayerStatsProfil Profil = new() { Id = "arcade/sf2ce/duel", Version = 1, Players = 2, Metrics = [Coups, Degats, Perfects] };

    private static readonly Dictionary<string, string> Textes = new()
    {
        ["multiplayer.stat.hits"] = "Coups", ["multiplayer.stat.damage"] = "Dégâts", ["multiplayer.stat.perfects"] = "Perfects",
        ["multiplayer.badge.perfects"] = "INTOUCHABLE", ["multiplayer.badge.category.efficacite"] = "AS DE L'OPTIMISATION",
        ["multiplayer.end.share.degats"] = "DÉGÂTS INFLIGÉS",
    };

    private static string T(string cle) => Textes.TryGetValue(cle, out var texte) ? texte : cle;

    private static IReadOnlyList<MultiplayerEcranDeFin.ValeurDeFin> Valeurs(long? coups, long? degats, long? perfects)
        =>
        [
            new(Coups, coups, coups is null ? MultiplayerStatsValeur.Indisponible : MultiplayerStatsValeur.Mesuree),
            new(Degats, degats, degats is null ? MultiplayerStatsValeur.Indisponible : MultiplayerStatsValeur.Mesuree),
            new(Perfects, perfects, perfects is null ? MultiplayerStatsValeur.Indisponible : perfects == 0 ? MultiplayerStatsValeur.Zero : MultiplayerStatsValeur.Mesuree),
        ];

    [Fact]
    public void Le_duel_SF2CE_donne_ses_etoiles_sa_distinction_et_sa_jauge()
    {
        var calcul = MultiplayerEcranDeFin.Calculer(Profil, new Dictionary<int, IReadOnlyList<MultiplayerEcranDeFin.ValeurDeFin>>
        {
            [1] = Valeurs(14, 288, 0),
            [2] = Valeurs(9, 190, 1),
        }, gagnante: 1, T);

        Assert.Equal([true, true, false], calcul.Mesures[1].Select(m => m.Meilleure));
        Assert.Equal([false, false, true], calcul.Mesures[2].Select(m => m.Meilleure));
        Assert.Equal("COUPS", calcul.Mesures[1][0].Libelle);
        Assert.Equal("gant", calcul.Mesures[1][0].Icone);
        Assert.Equal(9 / 14d, calcul.Mesures[2][0].Part, 3);
        // Le vainqueur, en tete de deux categories, n'a pas de distinction : il a la couronne.
        Assert.False(calcul.Distinctions.ContainsKey(1));
        Assert.Equal("INTOUCHABLE", calcul.Distinctions[2]);
        Assert.NotNull(calcul.Jauge);
        Assert.Equal("DÉGÂTS INFLIGÉS", calcul.Jauge!.Libelle);
        Assert.Equal(288 / 478d, calcul.Jauge.Parts.Single(p => p.Place == 1).Part, 3);
    }

    [Fact]
    public void Une_valeur_partagee_par_tous_n_a_pas_d_etoile()
    {
        var calcul = MultiplayerEcranDeFin.Calculer(Profil, new Dictionary<int, IReadOnlyList<MultiplayerEcranDeFin.ValeurDeFin>>
        {
            [1] = Valeurs(10, 200, 0),
            [2] = Valeurs(10, 100, 0),
        }, gagnante: 1, T);

        Assert.False(calcul.Mesures[1][0].Meilleure);
        Assert.False(calcul.Mesures[2][0].Meilleure);
        Assert.False(calcul.Mesures[1][2].Meilleure);
        Assert.Empty(calcul.Distinctions);
    }

    [Fact]
    public void Une_mesure_manquee_reste_non_mesuree_et_ne_donne_rien()
    {
        var calcul = MultiplayerEcranDeFin.Calculer(Profil, new Dictionary<int, IReadOnlyList<MultiplayerEcranDeFin.ValeurDeFin>>
        {
            [1] = Valeurs(null, 288, 0),
            [2] = Valeurs(9, null, 1),
        }, gagnante: 2, T);

        Assert.Null(calcul.Mesures[1][0].Valeur);
        Assert.Equal(0, calcul.Mesures[1][0].Part);
        // Une seule valeur connue de dégâts : pas d'étoile, pas de jauge à une place.
        Assert.False(calcul.Mesures[1][1].Meilleure);
        Assert.Null(calcul.Jauge);
        // P1 perd mais n'est en tete de rien de departage ; P2 gagne : aucune distinction.
        Assert.Empty(calcul.Distinctions);
    }

    [Fact]
    public void Le_nom_de_la_categorie_remplace_celui_d_une_mesure_sans_texte()
    {
        var calcul = MultiplayerEcranDeFin.Calculer(Profil, new Dictionary<int, IReadOnlyList<MultiplayerEcranDeFin.ValeurDeFin>>
        {
            [1] = Valeurs(14, 120, 1),
            [2] = Valeurs(9, 300, 0),
        }, gagnante: 1, T);

        Assert.Equal("AS DE L'OPTIMISATION", calcul.Distinctions[2]);
    }

    [Fact]
    public void Un_temps_garde_une_jauge_selon_l_ecart_au_meilleur()
    {
        var tour = new MultiplayerStatsMesure { Category = "efficacite", Id = "tour", Label = "tour", Better = "lower" };
        var profil = new MultiplayerStatsProfil { Id = "course", Version = 1, Players = 3, Metrics = [Coups, tour, Perfects] };
        var calcul = MultiplayerEcranDeFin.Calculer(profil, new Dictionary<int, IReadOnlyList<MultiplayerEcranDeFin.ValeurDeFin>>
        {
            [1] = [new(tour, 4126, MultiplayerStatsValeur.Mesuree)],
            [2] = [new(tour, 4240, MultiplayerStatsValeur.Mesuree)],
            [3] = [new(tour, 4183, MultiplayerStatsValeur.Mesuree)],
        }, gagnante: 2, T);

        var parts = calcul.Mesures.ToDictionary(m => m.Key, m => m.Value.Single(x => x.Libelle == "TOUR"));
        Assert.True(parts[1].Meilleure);
        Assert.Equal(1, parts[1].Part, 3);
        Assert.Equal(0.3, parts[2].Part, 3);
        // Un temps n'a pas de jauge globale : la part d'un total de temps ne veut rien dire.
        Assert.Null(calcul.Jauge);
    }

    [Theory]
    [InlineData("combat", "duel", "combat")]
    [InlineData("course", "course", "course")]
    [InlineData(null, "coop", "coop")]
    [InlineData("puzzle", "duel", "puzzle")]
    [InlineData("fps", "fps", "tir")]
    [InlineData("arene", "versus", "tir")]
    [InlineData(null, "versus", "combat")]
    public void Le_type_vient_du_type_de_cote_sinon_de_la_famille(string? cote, string famille, string attendu)
        => Assert.Equal(attendu, StylesDesEcrans.TypeDe(cote, famille));

    [Fact]
    public void Les_places_ont_leurs_couleurs_fixes()
    {
        Assert.Equal("a259ff", StylesDesEcrans.CouleurDeLaPlace(1));
        Assert.Equal("b6f03c", StylesDesEcrans.CouleurDeLaPlace(4));
        Assert.Equal("8a93a6", StylesDesEcrans.CouleurDeLaPlace(5));
        Assert.Equal(FormeDuPortrait.Bulle, StylesDesEcrans.Pour("puzzle").Forme);
        Assert.Equal("Titan One", StylesDesEcrans.Pour("puzzle").Police);
    }

    private static Bitmap Uni(Color fond, Color? zoneDuTitre = null)
    {
        var image = new Bitmap(192, 108);
        using var g = Graphics.FromImage(image);
        g.Clear(fond);
        if (zoneDuTitre is { } couleur)
        {
            using var pinceau = new SolidBrush(couleur);
            g.FillRectangle(pinceau, 0, 6, 100, 28);
        }

        return image;
    }

    [Fact]
    public void Le_titre_prend_la_couleur_vive_du_fanart_qui_se_detache_de_sa_zone()
    {
        // Du bleu vif partout, de l'orange sous le titre : le bleu se detache.
        using var fanart = Uni(Color.FromArgb(40, 100, 255), Color.FromArgb(255, 140, 40));
        Assert.Equal("3d7bff", CouleurDuTitre.Choisir(fanart, [1, 2], "d4ff3a").Titre);
    }

    [Fact]
    public void Le_titre_ne_prend_jamais_la_couleur_d_une_place_en_jeu()
    {
        // Tout orange : l'orange est la couleur de P2, il reste la couleur du type.
        using var fanart = Uni(Color.FromArgb(255, 138, 61));
        Assert.Equal("d4ff3a", CouleurDuTitre.Choisir(fanart, [1, 2], "d4ff3a").Titre);
        // A quatre, le vert du combat est celui de P4 : la couleur suivante de la palette.
        Assert.Equal("ffd23c", CouleurDuTitre.Choisir(fanart, [1, 2, 3, 4], "d4ff3a").Titre);
        // Sans fanart, la couleur du type.
        Assert.Equal("ff4fa3", CouleurDuTitre.Choisir(null, [1, 2, 3], "ff4fa3").Titre);
    }

    [Fact]
    public void Les_formes_restent_dans_leur_cadre_et_se_retournent()
    {
        var cadre = new RectangleF(10, 20, 150, 178);
        foreach (var forme in Enum.GetValues<FormeDuPortrait>())
        {
            using var chemin = FormesDesEcrans.Portrait(forme, cadre, miroir: false);
            var boite = chemin.GetBounds();
            Assert.True(boite.Left >= cadre.Left - 0.01 && boite.Right <= cadre.Right + 0.01 && boite.Top >= cadre.Top - 0.01 && boite.Bottom <= cadre.Bottom + 0.01, forme.ToString());
        }

        using var gauche = FormesDesEcrans.Portrait(FormeDuPortrait.Eclat, cadre, miroir: false);
        using var droite = FormesDesEcrans.Portrait(FormeDuPortrait.Eclat, cadre, miroir: true);
        Assert.Equal(cadre.Top + 0.07f * cadre.Height, gauche.PathPoints[0].Y, 2);
        Assert.Equal(cadre.Right, droite.PathPoints[0].X, 2);
    }

    [Fact]
    public void Les_icones_pixel_se_lisent_et_les_mesures_ont_la_leur()
    {
        Assert.Equal(9 / 5f, IconesPixel.Rapport("couronne"), 3);
        Assert.Equal("gant", IconesPixel.PourLaMesure("coups", "execution"));
        Assert.Equal("diamant", IconesPixel.PourLaMesure("inconnue", "maitrise"));
        using var image = new Bitmap(90, 50);
        using (var g = Graphics.FromImage(image))
        {
            IconesPixel.Dessiner(g, "couronne", new RectangleF(0, 0, 90, 50), Color.Red);
        }

        // La bande pleine du bas de la couronne (ligne 3 de la grille de 9 x 5, cases de 10 pixels).
        Assert.Equal(Color.Red.ToArgb(), image.GetPixel(45, 35).ToArgb());
        Assert.Equal(0, image.GetPixel(25, 5).A);
    }

    /// <summary>Le dossier des polices du depot (resources/theme/fonts), trouve en remontant depuis les binaires des tests.</summary>
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

    [Fact]
    public void Les_polices_livrees_portent_les_noms_que_les_ecrans_demandent()
    {
        PolicesDesEcrans.Dossier = DossierDesPolices();
        var noms = PolicesDesEcrans.Noms();
        foreach (var attendu in new[] { "Bangers", "Racing Sans One", "Bungee", "Titan One", "Graduate", "Cinzel Decorative", "Russo One",
                     StylesDesEcrans.PoliceDesChiffres, StylesDesEcrans.PoliceDesLibelles, "Barlow Condensed SemiBold" })
        {
            Assert.Contains(attendu, noms);
        }

        using var police = PolicesDesEcrans.Police(StylesDesEcrans.PoliceDesChiffres, 40, FontStyle.Italic);
        Assert.NotNull(police);
        Assert.True(police!.Italic);
        Assert.Null(PolicesDesEcrans.Police("Comic Sans Introuvable", 20));
    }

    // ── Les apercus ──────────────────────────────────────────────────────

    private static readonly string? Sortie = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS");
    private static readonly string? Images = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS_IMAGES");
    private static readonly string? Medias = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS_MEDIAS");

    [Fact]
    public void L_ecran_de_fin_se_dessine_a_chaque_temps_de_l_animation()
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
        var textes = new L.TextesDeFin("FIN DE PARTIE", "VAINQUEUR", "GG ! La revanche est à un bouton.", "MAINTENIR", "REVANCHE", "QUITTER");
        var calcul = MultiplayerEcranDeFin.Calculer(Profil, new Dictionary<int, IReadOnlyList<MultiplayerEcranDeFin.ValeurDeFin>>
        {
            [1] = Valeurs(14, 288, 0),
            [2] = Valeurs(9, 190, 1),
        }, 1, T);
        var duel = new L.EcranDeFin("salle-1", "combat", "VICTOIRE !", "Street Fighter II' Champion Edition", "1:01", Fanart("arcade", "sf2ce"),
        [
            new(1, "NELFE", "VEGA", Image("arcade/sf2ce/character/vega.png"), "A", "1 858", "+16", true, 1, "", calcul.Mesures[1]),
            new(2, "AKIRA", "GUILE", Image("arcade/sf2ce/character/guile.png"), "A", "1 780", "-16", false, 2, calcul.Distinctions[2], calcul.Mesures[2]),
        ], calcul.Jauge, "", "", textes, "button_a", "button_b");

        var tour = new MultiplayerStatsMesure { Category = "efficacite", Id = "tour", Label = "MEILLEUR TOUR", Better = "lower" };
        var vitesse = new MultiplayerStatsMesure { Category = "execution", Id = "vitesse", Label = "VITESSE MAX" };
        var depassements = new MultiplayerStatsMesure { Category = "maitrise", Id = "depassements", Label = "DÉPASSEMENTS" };
        var course = MultiplayerEcranDeFin.Calculer(new MultiplayerStatsProfil { Id = "course", Version = 1, Players = 4, Metrics = [vitesse, tour, depassements] },
            new Dictionary<int, IReadOnlyList<MultiplayerEcranDeFin.ValeurDeFin>>
            {
                [3] = [new(vitesse, 512, "measured"), new(tour, 4152, "measured"), new(depassements, 7, "measured")],
                [1] = [new(vitesse, 498, "measured"), new(tour, 4177, "measured"), new(depassements, 5, "measured")],
                [4] = [new(vitesse, 489, "measured"), new(tour, 4126, "measured"), new(depassements, 3, "measured")],
                [2] = [new(vitesse, 505, "measured"), new(tour, 4240, "measured"), new(depassements, 2, "measured")],
            }, 3, s => s);
        var machines = Image("gba/f-zero-maximum-velocity/machine");
        var quatre = new L.EcranDeFin("salle-2", "course", "1ER !", "F-Zero: Maximum Velocity", "2:14", Fanart("gba", "f_zero_maximum_velocity"),
        [
            new(3, "RAFALE", "J.B. CRYSTAL", Path.Combine(machines, "j-b-crystal.png"), "B", "1 554", "+22", true, 1, "", course.Mesures[3]),
            new(1, "NELFE", "HOT VIOLET", Path.Combine(machines, "hot-violet.png"), "B", "1 514", "+6", false, 2, course.Distinctions.GetValueOrDefault(1, ""), course.Mesures[1]),
            new(4, "MOMO", "WIND WALKER", Path.Combine(machines, "wind-walker.png"), "B", "1 490", "-5", false, 3, course.Distinctions.GetValueOrDefault(4, ""), course.Mesures[4]),
            new(2, "AKIRA", "FIRE BALL", Path.Combine(machines, "fire-ball.png"), "B", "1 442", "-18", false, 4, course.Distinctions.GetValueOrDefault(2, ""), course.Mesures[2]),
        ], new L.JaugeDeFin("DÉPASSEMENTS", [new(3, 0.41), new(1, 0.29), new(4, 0.18), new(2, 0.12)]), "", "", textes, "button_a", "button_b");

        var trois = new L.EcranDeFin("salle-3", "coop", "PARTIE TERMINÉE", "X-Men", "24:12", Fanart("arcade", "xmen"),
        [
            new(1, "NELFE", "WOLVERINE", Image("arcade/xmen/character/wolverine.png"), "", "", "", false, null, "", calcul.Mesures[1]),
            new(3, "MOMO", "STORM", Image("arcade/xmen/character/storm.png"), "", "", "", false, null, "INCREVABLE", calcul.Mesures[2]),
            new(2, "AKIRA", "", null, "", "", "", false, null, "", calcul.Mesures[2]),
        ], null, "Partie en mode Classique : le classement ne bouge pas.", "CLASSIQUE", textes, "button_a", "button_b");

        var socle = new L.ContenuMultiplayer(["VICTOIRE"], 0, false, "", "", "", null, [], -1, "", "", [], PleinEcran: true, Disposition: L.DispositionDesJoueurs.Colonnes);
        foreach (var temps in new[] { 0.2, 0.45, 0.9, 1.4, 2.0, 2.6, 2.85, 6.0 })
        {
            var png = L.ApercuMultiplayer(style, socle with { Fin = duel }, "", "", "", null!, temps);
            Assert.True(png.Length > 0);
            File.WriteAllBytes(Path.Combine(Sortie, $"fin-duel-{temps:0.00}s.png".Replace(',', '.')), png);
        }

        File.WriteAllBytes(Path.Combine(Sortie, "fin-quatre.png"), L.ApercuMultiplayer(style, socle with { Fin = quatre }, "", "", "", null!, 6));
        // Plusieurs personnages par place (option B du user, 30/10) : les suivants en vignettes sur le portrait.
        var equipes = duel with
        {
            Cle = "salle-4", Jeu = "Marvel vs. Capcom", Fanart = Fanart("arcade", "mvsc"),
            Joueurs =
            [
                duel.Joueurs[0] with { Personnage = "RYU / MEGA MAN", Portrait = Image("arcade/mvsc/character/ryu.png"),
                    Coequipiers = [Image("arcade/mvsc/character/mega-man.png")] },
                duel.Joueurs[1] with { Personnage = "WOLVERINE / HULK", Portrait = Image("arcade/mvsc/character/wolverine.png"),
                    Coequipiers = [Image("arcade/mvsc/character/hulk.png")] },
            ],
        };
        File.WriteAllBytes(Path.Combine(Sortie, "fin-equipes.png"), L.ApercuMultiplayer(style, socle with { Fin = equipes }, "", "", "", null!, 6));
        File.WriteAllBytes(Path.Combine(Sortie, "fin-trois.png"), L.ApercuMultiplayer(style, socle with { Fin = trois }, "", "", "", null!, 6));
        File.WriteAllBytes(Path.Combine(Sortie, "fin-sans-fanart.png"), L.ApercuMultiplayer(style, socle with { Fin = duel with { Fanart = null, Cle = "x" } }, "", "", "", null!, 6));
        // Un pseudo long ne mord pas sur les cartes (vu a la borne le 26/10).
        foreach (var (nom, pseudo) in new[] { ("fin-pseudo-long.png", "LORENZOLAMAS"), ("fin-pseudo-tres-long.png", "MEGAWARRIOR_MAXIMUS24") })
        {
            var long_ = duel with { Joueurs = [duel.Joueurs[0] with { Pseudo = pseudo }, .. duel.Joueurs.Skip(1)] };
            File.WriteAllBytes(Path.Combine(Sortie, nom), L.ApercuMultiplayer(style, socle with { Fin = long_ }, "", "", "", null!, 6));
        }
    }
}
