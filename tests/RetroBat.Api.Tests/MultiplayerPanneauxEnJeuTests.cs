using System.Drawing;
using System.Runtime.Versioning;
using System.Text.Json;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.Multiplayer;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES PANNEAUX EN JEU (CDC Ecrans v1.0, lot E3) : poses comme un decor de RetroBat, que RetroArch affiche lui-meme. Les
/// bandes, l'image a plat (centre transparent), le decor et son .info, la ligne du lanceur. L'apercu ne se dessine que si
/// NELFEPLAY_APERCUS donne un dossier.
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(PolicesPartagees.Nom)]
public class MultiplayerPanneauxEnJeuTests
{
    private static readonly MultiplayerGame Sf2ce = new() { Id = "arcade/sf2ce", System = "arcade", Set = "sf2ce", Players = 2, Family = "duel", EloType = "combat" };

    [Fact]
    public void Un_jeu_4_3_sur_un_ecran_16_9_laisse_deux_bandes_de_240_pixels()
    {
        var bandes = PanneauxEnJeu.Calculer(1920, 1080, MultiplayerDecor.FormatArcade);
        Assert.NotNull(bandes);
        Assert.Equal((240, 240, 1440), (bandes!.Gauche, bandes.Droite, bandes.Jeu));
        // Un ecran 5:4 n'a pas de bandes : pas de panneaux.
        Assert.Null(PanneauxEnJeu.Calculer(1280, 1024, MultiplayerDecor.FormatArcade));
    }

    [Fact]
    public void Seuls_les_jeux_d_arcade_horizontaux_a_un_ecran_ont_leurs_panneaux()
    {
        Assert.True(MultiplayerDecor.Concerne(Sf2ce));
        Assert.True(MultiplayerDecor.Concerne(Sf2ce with { Family = "equipes" }));
        Assert.False(MultiplayerDecor.Concerne(Sf2ce with { Family = "coop" }));
        Assert.False(MultiplayerDecor.Concerne(Sf2ce with { System = "gba" }));
        Assert.False(MultiplayerDecor.Concerne(Sf2ce with { Screens = 2 }));
    }

    [Theory]
    [InlineData(1842, 0.806)]
    [InlineData(1600, 0)]
    [InlineData(650, 0.928)]
    [InlineData(2100, 1)]
    public void La_barre_mene_a_la_lettre_suivante(double cote, double attendu)
        => Assert.Equal(attendu, MultiplayerDecor.Palier(cote), 2);

    [Fact]
    public void Le_ping_prend_une_couleur_d_etat()
    {
        Assert.Equal(Color.FromArgb(57, 229, 140), PanneauxEnJeu.CouleurDuPing(24));
        Assert.Equal(Color.FromArgb(255, 176, 46), PanneauxEnJeu.CouleurDuPing(80));
        Assert.Equal(Color.FromArgb(255, 79, 100), PanneauxEnJeu.CouleurDuPing(180));
        Assert.Equal(4, PanneauxEnJeu.BarresDuPing(24));
        Assert.Equal(1, PanneauxEnJeu.BarresDuPing(300));
    }

    [Fact]
    public void La_cote_porte_les_victoires_et_la_serie_quand_la_plateforme_les_envoie()
    {
        var cote = JsonSerializer.Deserialize<MultiplayerCote>("""{"value":1842,"matches":140,"rank":"A","wins":97,"streak":3}""")!;
        Assert.Equal((97, 3), (cote.Wins, cote.Streak));
        // Une plateforme plus ancienne ne les envoie pas : les panneaux ne les montrent pas.
        var ancienne = JsonSerializer.Deserialize<MultiplayerCote>("""{"value":1842,"matches":140}""")!;
        Assert.Null(ancienne.Wins);
        Assert.Null(ancienne.Streak);
    }

    [Fact]
    public void L_image_laisse_le_jeu_transparent_et_peint_les_bandes()
    {
        var bandes = PanneauxEnJeu.Calculer(960, 540, MultiplayerDecor.FormatArcade)!;
        using var image = PanneauxEnJeu.Dessiner(960, 540, bandes, StylesDesEcrans.Combat, null,
        [
            new(1, "NELFE", "", null, "A", "1 842", 0.8, 24),
            new(2, "AKIRA", "", null, "A", "1 796", 0.65, 31),
        ]);

        Assert.Equal(0, image.GetPixel(480, 270).A);
        Assert.Equal(0, image.GetPixel(bandes.Gauche + 2, 10).A);
        Assert.Equal(255, image.GetPixel(10, 500).A);
        Assert.Equal(255, image.GetPixel(950, 500).A);
    }

    [Fact]
    public void Le_decor_s_ecrit_pour_le_jeu_et_pour_son_systeme_avec_ses_marges()
    {
        var racine = Path.Combine(Path.GetTempPath(), "nelfeplay-decor-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bandes = new PanneauxEnJeu.Bandes(240, 240, 1440);
            using var image = new Bitmap(1920, 1080);
            Assert.True(MultiplayerDecor.Ecrire("fbneo", @"E:\RetroBat\roms\fbneo\sf2ce.zip", image, bandes, racine));
            Assert.True(File.Exists(Path.Combine(racine, "games", "fbneo", "sf2ce.png")));
            Assert.True(File.Exists(Path.Combine(racine, "systems", "fbneo.png")));
            using var info = JsonDocument.Parse(File.ReadAllText(Path.Combine(racine, "games", "fbneo", "sf2ce.info")));
            Assert.Equal(1920, info.RootElement.GetProperty("width").GetInt32());
            Assert.Equal(240, info.RootElement.GetProperty("left").GetInt32());
            Assert.Equal(240, info.RootElement.GetProperty("right").GetInt32());
            Assert.Equal(0, info.RootElement.GetProperty("top").GetInt32());
            // Un systeme qui n'est pas un nom ne s'ecrit pas.
            Assert.False(MultiplayerDecor.Ecrire("fb neo", @"E:\x\sf2ce.zip", image, bandes, racine));
            // A la fin de la partie, le decor s'efface : il n'apparait pas parmi les decors d'ES.
            MultiplayerDecor.Effacer(racine);
            Assert.False(Directory.Exists(racine));
        }
        finally
        {
            if (Directory.Exists(racine))
            {
                Directory.Delete(racine, recursive: true);
            }
        }
    }

    [Fact]
    public void La_ligne_du_lanceur_porte_le_decor_sauf_pour_une_vue_par_place()
    {
        var avecDecor = MultiplayerLaunchPlan.Arguments("", "fbneo", "fbneo", @"E:\roms\sf2ce.zip", Sf2ce, 1, [], 2, MultiplayerDecor.Nom);
        Assert.Contains("-bezel nelfeplay-multiplayer", avecDecor);
        var sansDecor = MultiplayerLaunchPlan.Arguments("", "fbneo", "fbneo", @"E:\roms\sf2ce.zip", Sf2ce, 1, []);
        Assert.DoesNotContain("-bezel", sansDecor);
        // Deux ecrans : la vue de la place garde « -bezel none », le decor ne s'ajoute pas.
        var deuxEcrans = MultiplayerLaunchPlan.Arguments("", "fbneo", "fbneo", @"E:\roms\x.zip", Sf2ce with { Screens = 2 }, 1, [], 2, MultiplayerDecor.Nom);
        Assert.Contains("-bezel none", deuxEcrans);
        Assert.DoesNotContain("nelfeplay-multiplayer", deuxEcrans);
    }

    private static readonly string? Sortie = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS");
    private static readonly string? Images = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS_IMAGES");
    private static readonly string? Medias = Environment.GetEnvironmentVariable("NELFEPLAY_APERCUS_MEDIAS");

    [Fact]
    public void Les_panneaux_se_dessinent_autour_du_jeu()
    {
        if (Sortie is null || Images is null || Medias is null)
        {
            return;   // outil de mise au point, hors des passes ordinaires
        }

        Directory.CreateDirectory(Sortie);
        for (var dossier = new DirectoryInfo(AppContext.BaseDirectory); dossier is not null; dossier = dossier.Parent)
        {
            if (Directory.Exists(Path.Combine(dossier.FullName, "resources", "theme", "fonts")))
            {
                PolicesDesEcrans.Dossier = Path.Combine(dossier.FullName, "resources", "theme", "fonts");
                break;
            }
        }

        var fanart = Path.Combine(Medias, "systems", "arcade", "games", "sf2ce", "artwork", "fanart.jpg");
        var capture = Path.Combine(Medias, "systems", "arcade", "games", "sf2ce", "artwork", "screenshot.png");
        var bandes = PanneauxEnJeu.Calculer(1920, 1080, MultiplayerDecor.FormatArcade)!;
        void Apercu(string nom, IReadOnlyList<PanneauxEnJeu.Joueur> joueurs)
        {
            using var panneaux = PanneauxEnJeu.Dessiner(1920, 1080, bandes, StylesDesEcrans.Combat, fanart, joueurs, new("VICTOIRES", "SÉRIE"));
            using var ecran = new Bitmap(1920, 1080);
            using (var g = Graphics.FromImage(ecran))
            {
                g.Clear(Color.Black);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                if (File.Exists(capture))
                {
                    using var jeu = Image.FromFile(capture);
                    g.DrawImage(jeu, bandes.Gauche, 0, bandes.Jeu, 1080);
                }

                g.DrawImage(panneaux, 0, 0);
            }

            ecran.Save(Path.Combine(Sortie, nom), System.Drawing.Imaging.ImageFormat.Png);
        }

        var vega = Path.Combine(Images, "arcade", "sf2ce", "character", "vega.png");
        var guile = Path.Combine(Images, "arcade", "sf2ce", "character", "guile.png");
        Apercu("jeu-duel.png", [new(1, "NELFE", "VEGA", vega, "A", "1 842", 0.81, 24, 128, 4), new(2, "AKIRA", "GUILE", guile, "A", "1 796", 0.65, 31, 97, 0)]);
        Apercu("jeu-sans-personnage.png", [new(1, "NELFE", "", null, "A", "1 842", 0.81, 24, 1204, 12), new(2, "AKIRA", "", null, "?", "1 012", null, 112, 3, 1)]);
        Apercu("jeu-quatre.png",
        [
            new(1, "NELFE", "VEGA", vega, "A", "1 842", 0.81, 24, 128, 4), new(2, "AKIRA", "GUILE", guile, "A", "1 796", 0.65, 31, 97, 0),
            new(3, "MOMO", "VEGA", vega, "C", "1 188", 0.63, 64, 12, 2), new(4, "RAFALE", "GUILE", guile, "B", "1 532", 0.77, 140),
        ]);
    }
}
