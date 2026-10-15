using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>La capture des parties : l'en-tete des films de RetroArch, le garde-fou et le reglage d'echelle.</summary>
public sealed class ReplayCaptureTests
{
    // Un en-tete Matroska minimal, comme RetroArch l'ecrit : segment de taille inconnue, pistes, une piste video
    // avec sa taille d'image et son rapport d'affichage, une piste son, puis la premiere grappe d'images.
    private static byte[] Film(int largeur, int hauteur, int? largeurAffichee, int? hauteurAffichee)
    {
        static byte[] Element(byte[] id, byte[] donnees) => [.. id, (byte)(0x80 | donnees.Length), .. donnees];
        static byte[] Entier2(int v) => [(byte)(v >> 8), (byte)v];

        var video = new List<byte>();
        video.AddRange(Element([0xB0], Entier2(largeur)));
        video.AddRange(Element([0xBA], Entier2(hauteur)));
        if (largeurAffichee is { } la && hauteurAffichee is { } ha)
        {
            video.AddRange(Element([0x54, 0xB0], [(byte)la]));
            video.AddRange(Element([0x54, 0xBA], [(byte)ha]));
            video.AddRange(Element([0x54, 0xB2], [3]));
        }
        var pisteVideo = Element([0xAE], [.. Element([0xD7], [1]), .. Element([0x83], [1]), .. Element([0xE0], [.. video])]);
        var pisteSon = Element([0xAE], [.. Element([0xD7], [2]), .. Element([0x83], [2])]);
        var pistes = Element([0x16, 0x54, 0xAE, 0x6B], [.. pisteVideo, .. pisteSon]);
        byte[] entete = [0x1A, 0x45, 0xDF, 0xA3, 0x84, 0x42, 0x86, 0x81, 0x01];
        byte[] segmentInconnu = [0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        byte[] grappe = [0x1F, 0x43, 0xB6, 0x75, 0x81, 0x00];
        return [.. entete, .. segmentInconnu, .. pistes, .. grappe];
    }

    [Fact]
    public void Un_jeu_vertical_filme_par_retroarch_est_reconnu_couche()
    {
        // Ms. Pac-Man, mesure du 14 octobre : image 1152x896, affichage 3:4.
        var film = Film(1152, 896, 3, 4);
        var entete = MatroskaEntete.Lire(film, film.Length);
        Assert.Equal(new EnteteVideo(1152, 896, 3, 4), entete);
        Assert.True(entete!.Couchee);
    }

    [Fact]
    public void Un_jeu_horizontal_reste_droit_avec_ou_sans_rapport_d_affichage()
    {
        // Double Dragon : 1024x960 affiche en 4:3.
        var film = Film(1024, 960, 4, 3);
        Assert.False(MatroskaEntete.Lire(film, film.Length)!.Couchee);
        // Sans rapport d'affichage (capture de l'image affichee), l'image s'affiche telle quelle.
        var sansRapport = Film(648, 864, null, null);
        var entete = MatroskaEntete.Lire(sansRapport, sansRapport.Length);
        Assert.Equal(new EnteteVideo(648, 864, 648, 864), entete);
        Assert.False(entete!.Couchee);
    }

    [Fact]
    public void Un_film_tronque_ou_d_un_autre_format_ne_donne_rien()
    {
        var film = Film(1024, 960, 4, 3);
        Assert.Null(MatroskaEntete.Lire(film, 20));
        Assert.Null(MatroskaEntete.Lire("ceci n'est pas un film"u8.ToArray(), 22));
    }

    [Fact]
    public void Le_garde_fou_laisse_filmer_un_jeu_a_sa_cadence_meme_en_pause()
    {
        var garde = new GardeDeCapture();
        long frame = 0;
        var t = 0.0;
        for (var i = 0; i < 20; i++)
        {
            t += 2;
            frame += 115; // 57,5 images/s, Double Dragon
            Assert.False(garde.Relever(t, frame));
        }
        // Une pause de dix secondes : aucune image, rien a juger.
        for (var i = 0; i < 5; i++)
        {
            t += 2;
            Assert.False(garde.Relever(t, frame));
        }
        // L'avance rapide ne compte pas non plus.
        t += 2;
        frame += 600;
        Assert.False(garde.Relever(t, frame));
        Assert.Equal(57.5, garde.Reference, 1);
    }

    [Fact]
    public void Le_garde_fou_arrete_la_capture_quand_le_jeu_ralentit()
    {
        var garde = new GardeDeCapture();
        long frame = 0;
        var t = 0.0;
        for (var i = 0; i < 5; i++)
        {
            t += 2;
            frame += 120; // 60 images/s
            garde.Relever(t, frame);
        }
        // Le jeu tombe a 48 images/s : il faut trois releves de suite pour arreter.
        t += 2; frame += 96;
        Assert.False(garde.Relever(t, frame));
        t += 2; frame += 96;
        Assert.False(garde.Relever(t, frame));
        t += 2; frame += 96;
        Assert.True(garde.Relever(t, frame));
    }

    [Fact]
    public void Une_borne_lente_des_le_depart_est_reperee_aussi()
    {
        // 44 images/s des le debut : aucune machine suivie ne joue a cette cadence.
        var garde = new GardeDeCapture();
        long frame = 0;
        var t = 0.0;
        var arret = false;
        for (var i = 0; i < 4 && !arret; i++)
        {
            t += 2;
            frame += 88;
            arret = garde.Relever(t, frame);
        }
        Assert.True(arret);
    }

    [Fact]
    public void Une_reprise_d_envoi_part_de_l_octet_suivant_le_dernier_recu()
    {
        Assert.Equal(1048576, ReplayVideoPublisher.OctetsRecus("bytes=0-1048575"));
        Assert.Equal(0, ReplayVideoPublisher.OctetsRecus(null));
        Assert.Equal(0, ReplayVideoPublisher.OctetsRecus(""));
        Assert.Equal(0, ReplayVideoPublisher.OctetsRecus("illisible"));
    }

    [Fact]
    public void Une_fiche_d_avant_la_reprise_se_relit_sans_session_ni_video()
    {
        const string ancienne = """{"ReplayId":"rp_A","Fichier":"C:\\captures\\rp_A.mkv","DebutFilm":12,"FinFilm":9000,"CreeeUtc":"2026-10-14T16:00:00Z"}""";
        var g = System.Text.Json.JsonSerializer.Deserialize<ReplayCaptureService.CaptureGardee>(ancienne);
        Assert.NotNull(g);
        Assert.Equal("rp_A", g!.ReplayId);
        Assert.Null(g.Session);
        Assert.Null(g.VideoId);
        Assert.Null(g.EnvoyeeUtc);
        // D'avant le film sans replay : une partie avec replay, sans sessions.
        Assert.False(g.SansReplay);
        Assert.Null(g.Sessions);
    }

    [Fact]
    public void Le_film_d_une_partie_sans_replay_garde_ses_sessions()
    {
        var g = new ReplayCaptureService.CaptureGardee("fm_A", @"C:\captures\fm_A.mkv", 0, 0,
            new DateTime(2026, 10, 14, 20, 23, 0, DateTimeKind.Utc), Sessions: ["sess-1lc", "sess-1cc"], SansReplay: true);
        var relue = System.Text.Json.JsonSerializer.Deserialize<ReplayCaptureService.CaptureGardee>(
            System.Text.Json.JsonSerializer.Serialize(g));
        Assert.NotNull(relue);
        Assert.True(relue!.SansReplay);
        Assert.Equal(["sess-1lc", "sess-1cc"], relue.Sessions);
        Assert.StartsWith(ReplayCaptureService.PrefixeSansReplay, relue.ReplayId);
    }

    [Theory]
    // Le plus petit facteur entier qui porte le petit cote a 720 lignes (YouTube : 60 images par seconde au-dela).
    [InlineData(256, 224, 4)]   // arcade, Mega Drive, Super Nintendo : 896 lignes
    [InlineData(256, 240, 3)]   // NES, Double Dragon : 720 lignes
    [InlineData(224, 384, 4)]   // 19XX sous MAME, deja vertical : 896 de large
    [InlineData(160, 144, 5)]   // Game Boy : 800x720
    [InlineData(240, 160, 5)]   // Game Boy Advance : 1200x800
    [InlineData(160, 102, 8)]   // Lynx : 1280x816
    [InlineData(640, 480, 2)]   // haute definition : 1280x960
    [InlineData(1280, 720, 1)]
    [InlineData(96, 64, 8)]     // Pokemon Mini : plafonne a x8
    [InlineData(0, 0, 4)]       // inconnu : x4
    public void Le_facteur_du_film_atteint_720_lignes_au_plus_juste(int largeur, int hauteur, int attendu)
        => Assert.Equal(attendu, EchelleDesFilms.Facteur(largeur, hauteur));

    [Fact]
    public void La_definition_du_jeu_se_lit_dans_le_journal_de_lancement()
    {
        string[] journal =
        [
            "[INFO] [Content] Content loading skipped. Implementation will load it on its own.",
            "[INFO] [Core] Geometry: 224x384, Aspect: 0.750, FPS: 59.64, Sample rate: 48000.00 Hz.",
            "[INFO] [Video] Set video size to: 1920x1200.",
        ];
        Assert.Equal((224, 384), EchelleDesFilms.GeometrieDuJournal(journal));
        Assert.Null(EchelleDesFilms.GeometrieDuJournal(["[INFO] [Video] Set video size to: 1920x1200."]));
    }

    [Fact]
    public void Le_jeu_lance_se_lit_dans_les_arguments_du_script_game_start()
    {
        Assert.Equal(("gb", @"E:\RetroBat\roms\gb\Tetris (World).zip"),
            EchelleDesFilms.LancementDuScript("\"E:\\RetroBat\\roms\\gb\\Tetris (World).zip\" \"Tetris (World)\" \"Tetris\""));
        Assert.Equal(("mame", @"E:\RetroBat\roms\mame\19xx.zip"),
            EchelleDesFilms.LancementDuScript("mame \"E:\\RetroBat\\roms\\mame\\19xx.zip\" 19xx"));
        Assert.Equal(((string?)null, (string?)null), EchelleDesFilms.LancementDuScript(""));
    }

    [Fact]
    public void Au_demarrage_seul_un_facteur_absent_ou_d_origine_est_remplace()
    {
        Assert.Equal("5", ReplayCaptureService.ValeurDans("a = \"1\"\nvideo_record_scale_factor = \"5\"\n", "video_record_scale_factor"));
        Assert.Null(ReplayCaptureService.ValeurDans("a = \"1\"\n", "video_record_scale_factor"));
    }

    [Fact]
    public void La_trame_emulee_se_lit_dans_un_signal_du_plugin_lua_de_mame()
    {
        // La forme que publie MameLuaIngameProvider (ingame.memory.changed).
        var signal = new { Source = "mame.lua", SystemId = "mame", Rom = "19xx", signal = new { Key = "SCORE", Value = 300L, Frame = (long?)4512 } };
        Assert.Equal(("mame.lua", 4512L), ReplayCaptureService.TrameDuJeu(signal));

        // Un plugin d'avant la trame, ou un signal sans trame : rien a suivre.
        Assert.Null(ReplayCaptureService.TrameDuJeu(new { Source = "mame.lua", signal = new { Key = "SCORE", Frame = (long?)null } }));
        Assert.Null(ReplayCaptureService.TrameDuJeu(new { Source = "mame.lua", signal = new { Key = "SCORE" } }));
        Assert.Null(ReplayCaptureService.TrameDuJeu(new { signal = new { Frame = 10L } }));
        Assert.Null(ReplayCaptureService.TrameDuJeu(null));
    }

    [Fact]
    public void L_echelle_des_films_se_pose_une_seule_fois_dans_retroarch_cfg()
    {
        const string avant = "record_driver = \"ffmpeg\"\r\nvideo_record_scale_factor = \"1\"\r\nvideo_record_threads = \"2\"\r\n";
        var apres = ReplayCaptureService.AvecReglage(avant, "video_record_scale_factor", "4");
        Assert.Equal("record_driver = \"ffmpeg\"\r\nvideo_record_scale_factor = \"4\"\r\nvideo_record_threads = \"2\"\r\n", apres);
        Assert.Null(ReplayCaptureService.AvecReglage(apres!, "video_record_scale_factor", "4"));
        // Absent : ajoute avant la derniere ligne vide, sans toucher a video_record_scale_factor_x eventuel.
        var sans = ReplayCaptureService.AvecReglage("a = \"1\"\n", "video_record_scale_factor", "4");
        Assert.Equal("a = \"1\"\nvideo_record_scale_factor = \"4\"\n", sans);
    }

    [Fact]
    public void Le_film_a_la_taille_d_origine_se_pose_dans_retroarch_cfg_et_se_retire_au_retour_du_film_agrandi()
    {
        const string cfg = @"E:\RetroBat\plugins\APIExpose\state\nelfenet\replay\film-origine.cfg";
        // La borne du 15 octobre : qualite moyenne de RetroArch, sans fichier de reglages.
        const string avant = "video_gpu_record = \"false\"\r\nvideo_record_config = \"\"\r\nvideo_record_quality = \"2\"\r\n"
                             + "video_record_scale_factor = \"4\"\r\n";
        var origine = ReplayCaptureService.ReglagesDesFilms(avant, true, cfg);
        Assert.Equal("video_gpu_record = \"false\"\r\nvideo_record_config = \"" + cfg + "\"\r\nvideo_record_quality = \"0\"\r\n"
                     + "video_record_scale_factor = \"4\"\r\n", origine);
        Assert.Null(ReplayCaptureService.ReglagesDesFilms(origine!, true, cfg));

        // Retour au film agrandi : la qualite moyenne revient, le fichier n'est plus designe.
        Assert.Equal(avant, ReplayCaptureService.ReglagesDesFilms(origine!, false, cfg));
        Assert.Null(ReplayCaptureService.ReglagesDesFilms(avant, false, cfg));
        // Un fichier de reglages du joueur n'est pas le notre : on n'y touche pas.
        Assert.Null(ReplayCaptureService.ReglagesDesFilms(
            "video_record_config = \"E:\\mes-reglages.cfg\"\nvideo_record_quality = \"0\"\n", false, cfg));
    }

    [Fact]
    public void Les_reglages_du_film_a_la_taille_d_origine_sont_ceux_mesures_le_15_octobre()
    {
        var reglages = ReplayCaptureService.ReglagesDuFilm;
        foreach (var attendu in new[] { "vcodec = \"libx264\"", "pix_fmt = \"yuv444p\"", "format = \"matroska\"", "scale_factor = \"1\"",
                     "video_preset = \"medium\"", "video_tune = \"animation\"", "video_crf = \"14\"", "acodec = \"aac\"" })
            Assert.Contains(attendu + "\n", reglages);
        Assert.DoesNotContain("\r", reglages);
    }

    [Theory]
    [InlineData(@"C:\captures\01J.flv", "video/x-flv")]
    [InlineData(@"C:\captures\01J.FLV", "video/x-flv")]
    [InlineData(@"C:\captures\01J.mkv", "video/x-matroska")]
    public void Le_type_du_film_envoye_suit_son_format(string fichier, string attendu)
        => Assert.Equal(attendu, ReplayVideoPublisher.TypeDuFilm(fichier));

    [Fact]
    public void La_version_youtube_se_fabrique_du_debut_du_film_dans_le_sens_du_jeu()
        => Assert.Equal(["--film", @"C:\c\01J.mkv", "--sortie-flv", @"C:\c\01J.flv", "--depuis-le-debut", "--rotation", "3"],
            LanceurDuDirect.ArgumentsDeLaVersion(@"C:\c\01J.mkv", @"C:\c\01J.flv", 3));
}

/// <summary>GET_STATUS evite quand le coeur charge n'a pas d'identifiant de systeme (plantage de RetroArch 1.22.2).</summary>
public sealed class GardeGetStatusTests
{
    [Fact]
    public void Le_coeur_charge_se_lit_dans_le_journal_de_lancement()
    {
        string[] journal =
        [
            "[INFO] RetroArch 1.22.2 (Git 69a4f0e)",
            @"[INFO] [Core] Loading dynamic libretro core from: ""H:\RetroBat\emulators\retroarch\core_proxy\amiberry_libretro.dll"".",
            "[INFO] [Environ] SET_MEMORY_MAPS.",
        ];
        Assert.Equal(@"H:\RetroBat\emulators\retroarch\core_proxy\amiberry_libretro.dll",
            RetroBat.Api.Replay.Runtime.GardeGetStatus.CoeurDuJournal(journal));
        Assert.Null(RetroBat.Api.Replay.Runtime.GardeGetStatus.CoeurDuJournal(["[INFO] RetroArch 1.22.2 (Git 69a4f0e)"]));
    }

    [Fact]
    public void Un_info_sans_identifiant_de_systeme_rend_get_status_dangereux()
    {
        Assert.True(RetroBat.Api.Replay.Runtime.GardeGetStatus.DeclareUnSysteme("display_name = \"Amiga (PUAE)\"\nsystemid = \"commodore_amiga\"\n"));
        Assert.False(RetroBat.Api.Replay.Runtime.GardeGetStatus.DeclareUnSysteme("display_name = \"Amiga (Amiberry)\"\n"));
        Assert.False(RetroBat.Api.Replay.Runtime.GardeGetStatus.DeclareUnSysteme("systemid = \"\"\n"));
        Assert.False(RetroBat.Api.Replay.Runtime.GardeGetStatus.DeclareUnSysteme(null));
    }
}
