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
}
