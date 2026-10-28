using RetroBat.Api.Replay.Runtime;
using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES FILMS DE GOGERAD APLATIS (25/10) : le filtre NTSC de Genesis Plus GX sort 640 x 224 (Sonic) et 602 x 192 (Alex Kidd)
/// au lieu de 320 x 224 et 256 x 192. Un filtre actif coupe film et direct ; la forme du coeur redresse le reste ; un film
/// dont la forme reste fausse ne part pas ; le rendu d'un replay se fait filtres coupes.
/// </summary>
public class FiltreDImageTests
{
    private static readonly string[] Options =
    [
        "genesis_plus_gx_aspect_ratio = \"auto\"",
        "genesis_plus_gx_blargg_ntsc_filter = \"composite\"",
        "snes9x_blargg = \"disabled\"",
        "nestopia_blargg_ntsc_filter = \"composite\"",
        "fbneo-diagnostic-input = \"None\"",
    ];

    [Fact]
    public void Seul_le_filtre_du_coeur_charge_compte()
    {
        Assert.Equal("genesis_plus_gx_blargg_ntsc_filter = composite", FiltreDImage.Actif("genesis_plus_gx", Options));
        // La variante partage les options du coeur d'origine.
        Assert.Equal("genesis_plus_gx_blargg_ntsc_filter = composite", FiltreDImage.Actif("genesis_plus_gx_expanded", Options));
        // Le filtre d'une autre console ne coupe pas le film de celle-ci.
        Assert.Null(FiltreDImage.Actif("snes9x", Options));
        Assert.Null(FiltreDImage.Actif("fbneo", Options));
        Assert.Null(FiltreDImage.Actif(null, Options));
        Assert.Equal("nestopia_blargg_ntsc_filter = composite", FiltreDImage.Actif("nestopia", Options));
    }

    [Fact]
    public void Le_rendu_d_un_replay_coupe_les_filtres_et_garde_le_reste()
    {
        var sans = FiltreDImage.SansFiltres(Options).ToArray();
        Assert.Equal("genesis_plus_gx_blargg_ntsc_filter = \"disabled\"", sans[1]);
        Assert.Equal("nestopia_blargg_ntsc_filter = \"disabled\"", sans[3]);
        Assert.Equal(Options[0], sans[0]);
        Assert.Equal(Options[2], sans[2]);
        Assert.Equal(Options[4], sans[4]);
        Assert.Null(FiltreDImage.Actif("genesis_plus_gx", sans));

        var profil = ReplayVideoRenderer.ProfilDeRendu(@"C:\rendu\sauvegardes", @"C:\rendu\films", @"C:\rendu\options-des-coeurs.cfg");
        Assert.Contains("core_options_path = \"C:\\rendu\\options-des-coeurs.cfg\"", profil);
        Assert.Contains("global_core_options = \"true\"", profil);
        Assert.DoesNotContain("core_options_path", ReplayVideoRenderer.ProfilDeRendu(@"C:\rendu\sauvegardes", @"C:\rendu\films"));
    }

    [Fact]
    public void Le_journal_donne_la_forme_et_le_coeur_de_la_partie()
    {
        var lignes = new[]
        {
            "[INFO] [Core] Loading dynamic libretro core from: \"E:\\RetroBat\\emulators\\retroarch\\core_proxy\\genesis_plus_gx_libretro.dll\".",
            "[INFO] [Content] CRC32: 0x50ABC90F.",
            "[INFO] [Core] Geometry: 602x192, Aspect: 1.524, FPS: 59.92, Sample rate: 44100.00 Hz.",
        };
        var cadence = ReplayCoreTimingProbe.LireLaCadence(lignes)!;
        Assert.Equal(602, cadence.Width);
        Assert.Equal(1.524, cadence.Aspect);
        Assert.Equal("genesis_plus_gx", cadence.Coeur);
        Assert.Equal("50abc90f", cadence.Crc32);
        Assert.Equal(59.92, cadence.Fps);
    }

    [Fact]
    public void La_forme_suit_la_derniere_geometrie_annoncee_par_le_coeur()
    {
        // mGBA Splitscreen en vue « focus » (28/10, direct de Mario Kart etire en 3:1) : 480x160 au chargement, puis la vue de
        // la place, 480x320, a la premiere image. Le film prend la geometrie du moment ou il commence.
        var lignes = new[]
        {
            "[INFO] [Core] Loading dynamic libretro core from: \"E:\\RetroBat\\emulators\\retroarch\\cores\\mgba_splitscreen_libretro.dll\".",
            "[INFO] [Content] CRC32: 0xed316e37.",
            "[INFO] [Core] Geometry: 480x160, Aspect: 3.000, FPS: 59.73, Sample rate: 32768.00 Hz.",
            "[libretro INFO] [splitscreen] view: focus, focused player 1",
            "[INFO] [Environ] SET_SYSTEM_AV_INFO: 480x320, Aspect: 1.500, FPS: 59.73, Sample rate: 32768.00 Hz.",
        };
        var cadence = ReplayCoreTimingProbe.LireLaCadence(lignes)!;
        Assert.Equal((480, 320, 1.5), (cadence.Width, cadence.Height, cadence.Aspect));
        Assert.Equal(59.73, cadence.Fps);
        Assert.Equal("ed316e37", cadence.Crc32);

        var geometrie = ReplayCoreTimingProbe.LireLaCadence([.. lignes, "[INFO] [Environ] SET_GEOMETRY: 240x160, Aspect: 1.500."])!;
        Assert.Equal((240, 160, 1.5), (geometrie.Width, geometrie.Height, geometrie.Aspect));
        // Une annonce avant le chargement d'un contenu ne decrit aucune partie.
        Assert.Null(ReplayCoreTimingProbe.LireLaCadence(["[INFO] [Environ] SET_SYSTEM_AV_INFO: 480x320, Aspect: 1.500, FPS: 59.73, Sample rate: 32768.00 Hz."]));
    }

    [Theory]
    // Sans forme connue : une image deux fois plus large que haute ne part pas.
    [InlineData(640, 224, null, null, true, true)]
    [InlineData(320, 224, null, null, true, false)]
    // Avec la forme : l'exe 0.5.8 la rend, un exe plus ancien l'etirerait.
    [InlineData(602, 192, null, 1.524, true, false)]
    [InlineData(602, 192, null, 1.524, false, true)]
    [InlineData(256, 192, null, 1.524, false, false)]
    // Un jeu tourne garde son redressement d'avant.
    [InlineData(224, 288, 3, 1.333, false, false)]
    public void Un_film_a_la_forme_fausse_ne_part_pas(int largeur, int hauteur, int? rotation, double? forme, bool exe, bool refuse)
        => Assert.Equal(refuse, ReplayCaptureService.FormeRefusee(largeur, hauteur, rotation, forme, exe) is not null);

    [Fact]
    public void L_exe_recoit_la_forme_du_coeur()
    {
        var version = LanceurDuDirect.ArgumentsDeLaVersion("film.mkv", "film.flv", 0, forme: 1.524);
        Assert.Equal(["--forme", "1.524"], version.TakeLast(2));
        var direct = LanceurDuDirect.Arguments("film.mkv", 0, forme: 1.3061);
        Assert.Equal(["--forme", "1.3061"], direct.TakeLast(2));
        Assert.DoesNotContain("--forme", LanceurDuDirect.Arguments("film.mkv", 0));
    }
}
