using System.Drawing;
using RetroBat.Api.Leaderboard;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'IMAGE DU BANDEAU D'UN CHALLENGE : l'image de l'epreuve d'abord, celle du jeu ensuite, rien sinon ; Composer la pose
/// sur la ligne, et le recadrage 16:9 ne deforme pas.
/// </summary>
public class ImagesDesDefisTests : IDisposable
{
    private static readonly Reglement MetalSlug = Reglement.Lire(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Reglements", "metal-slug-3.mission-1-knife.json")));

    private static readonly Reglement GreenHill1 = Reglement.Lire(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Reglements", "sonic-the-hedgehog.green-hill-1.json")));

    private readonly string _racine = Path.Combine(Path.GetTempPath(), "images-defis-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_racine, true); } catch (IOException) { }
    }

    private string PoserSurLEtat(string extension)
    {
        var chemin = Path.Combine(_racine, "arcade", "metal-slug-3", "mission-1-depart" + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllBytes(chemin, [1]);
        return chemin;
    }

    private string ImageDuJeu()
    {
        var chemin = Path.Combine(_racine, "jeu.png");
        Directory.CreateDirectory(_racine);
        File.WriteAllBytes(chemin, [2]);
        return chemin;
    }

    [Fact]
    public void L_image_de_l_epreuve_passe_avant_celle_du_jeu()
    {
        var propre = PoserSurLEtat(".png");
        var jeu = ImageDuJeu();

        var images = ImagesDesDefis.Pour([MetalSlug], "roms/mame/mslug3.zip", _racine, _ => jeu);

        Assert.Equal(propre, images[MetalSlug.ChallengeId]);
    }

    [Fact]
    public void Un_jpg_a_cote_de_l_etat_est_accepte()
    {
        var propre = PoserSurLEtat(".jpg");

        Assert.Equal(propre, ImagesDesDefis.DeLEpreuve(MetalSlug, _racine));
    }

    [Fact]
    public void Sans_image_d_epreuve_l_image_du_jeu_est_prise()
    {
        var jeu = ImageDuJeu();

        var images = ImagesDesDefis.Pour([MetalSlug], "roms/mame/mslug3.zip", _racine, _ => jeu);

        Assert.Equal(jeu, images[MetalSlug.ChallengeId]);
    }

    [Fact]
    public void Sans_image_ni_d_epreuve_ni_de_jeu_il_n_y_a_rien()
    {
        var images = ImagesDesDefis.Pour([MetalSlug], "roms/mame/mslug3.zip", _racine, _ => null);

        Assert.Empty(images);
    }

    [Fact]
    public void Un_speedrun_n_a_pas_de_bandeau_et_l_image_du_jeu_n_est_cherchee_qu_une_fois()
    {
        var jeu = ImageDuJeu();
        var appels = 0;

        var images = ImagesDesDefis.Pour([GreenHill1, MetalSlug, MetalSlug], "x", _racine, _ => { appels++; return jeu; });

        Assert.Equal(jeu, Assert.Single(images).Value);
        Assert.Equal(1, appels);
    }

    [Fact]
    public void Composer_pose_l_image_sur_le_bandeau_du_challenge_et_laisse_vide_sans_image()
    {
        var service = new DefisPanelService(null!, null!, null!, null!, null!);
        var bilan = new Dictionary<string, (int, long?)>();

        var avec = service.Composer([MetalSlug], bilan, "en", images: new Dictionary<string, string> { [MetalSlug.ChallengeId] = "C:/a.png" });
        var sans = service.Composer([MetalSlug], bilan, "en");

        Assert.Equal("C:/a.png", avec[DefisPanelModel.Onglet.Challenges][1].Ligne.Image);
        Assert.Null(sans[DefisPanelModel.Onglet.Challenges][1].Ligne.Image);
    }

    [Theory]
    [InlineData(640, 480, 0, 60, 640, 360)]    // 4:3 : on perd le haut et le bas
    [InlineData(1920, 1080, 0, 0, 1920, 1080)] // deja 16:9
    [InlineData(2000, 800, 289, 0, 1422, 800)] // plus large : on perd les cotes
    [InlineData(240, 256, 0, 60, 240, 135)]    // NES : plus haute que large
    public void La_zone_16_9_est_centree_sans_deformation(int l, int h, int x, int y, int zl, int zh)
    {
        var zone = EsGlyphCache.Zone169(l, h);

        Assert.Equal(new Rectangle(x, y, zl, zh), zone);
    }

    [Fact]
    public void La_vignette_fait_16_9_a_la_hauteur_demandee()
    {
        Directory.CreateDirectory(_racine);
        var chemin = Path.Combine(_racine, "capture.png");
        using (var source = new Bitmap(320, 240)) source.Save(chemin, System.Drawing.Imaging.ImageFormat.Png);

        using var vignette = EsGlyphCache.Recadrer169(chemin, 90);

        Assert.Equal(90, vignette.Height);
        Assert.Equal(160, vignette.Width);
    }
}
