using System.Buffers.Binary;
using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Redresser un film vertical de RetroArch en corrigeant l'en-tete Matroska, sans le reencoder.</summary>
public sealed class MatroskaRotationTests
{
    // Un debut de fichier construit comme celui de RetroArch 1.22 (Ms. Pac-Man, mesure du 14 octobre) : segment de
    // taille connue, en-tete de recherche et pistes proteges par une somme CRC-32, reserve de 149 octets avant les
    // informations, piste video en-tete long (taille sur 8 octets) avec un affichage 3:4 sur une image 1152x896,
    // donnees du codec apres la piste video, piste son, puis la premiere grappe d'images.
    private static readonly byte[] Grappe = [0x1F, 0x43, 0xB6, 0x75, 0x84, 0xA3, 0x82, 0x81, 0x00];

    private static byte[] El(byte[] id, byte[] donnees, int octetsDeTaille = 0)
    {
        var n = octetsDeTaille > 0 ? octetsDeTaille : donnees.Length < 127 ? 1 : 2;
        var t = new byte[n];
        long v = donnees.Length;
        for (var k = n - 1; k >= 0; k--) { t[k] = (byte)(v & 0xFF); v >>= 8; }
        t[0] |= (byte)(0x80 >> (n - 1));
        return [.. id, .. t, .. donnees];
    }

    private static byte[] AvecCrc(byte[] id, byte[] enfants, int octetsDeTaille = 0)
    {
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(crc, MatroskaRotation.Crc32(enfants, 0, enfants.Length));
        return El(id, [.. El([0xBF], crc), .. enfants], octetsDeTaille);
    }

    private static int Chercher(byte[] dans, params byte[] motif) => dans.AsSpan().IndexOf(motif);

    private static byte[] U(long v, int n)
    {
        var o = new byte[n];
        for (var k = n - 1; k >= 0; k--) { o[k] = (byte)(v & 0xFF); v >>= 8; }
        return o;
    }

    /// <summary>Le debut de fichier, et les positions (dans le segment) des informations et des pistes.</summary>
    private static (byte[] Fichier, int OrigineSegment) DebutDeFilm()
    {
        var info = AvecCrc([0x15, 0x49, 0xA9, 0x66], [.. El([0x2A, 0xD7, 0xB1], U(1000000, 3))]);
        var video = El([0xE0], [.. El([0xB0], U(1152, 2)), .. El([0xBA], U(896, 2)), .. El([0x54, 0xB0], [3]),
            .. El([0x54, 0xBA], [4]), .. El([0x54, 0xB2], [3])], 8);
        var pisteVideo = El([0xAE], [.. El([0xD7], [1]), .. El([0x83], [1]), .. El([0x86], "V_MPEG4/ISO/AVC"u8.ToArray()),
            .. video, .. El([0x63, 0xA2], new byte[30])], 8);
        var pisteSon = El([0xAE], [.. El([0xD7], [2]), .. El([0x83], [2]), .. El([0x86], "A_AAC"u8.ToArray())], 8);
        var pistes = AvecCrc([0x16, 0x54, 0xAE, 0x6B], [.. pisteVideo, .. pisteSon], 2);
        var vide = El([0xEC], new byte[149], 8);
        // L'en-tete de recherche fait toujours la meme longueur : on le construit deux fois pour y mettre les vraies
        // positions (Info et Tracks juste apres lui et la reserve ; Cues, loin dans le fichier).
        byte[] Recherche(long posInfo, long posPistes) => AvecCrc([0x11, 0x4D, 0x9B, 0x74], [
            .. El([0x4D, 0xBB], [.. El([0x53, 0xAB], [0x15, 0x49, 0xA9, 0x66]), .. El([0x53, 0xAC], U(posInfo, 2))]),
            .. El([0x4D, 0xBB], [.. El([0x53, 0xAB], [0x16, 0x54, 0xAE, 0x6B]), .. El([0x53, 0xAC], U(posPistes, 2))]),
            .. El([0x4D, 0xBB], [.. El([0x53, 0xAB], [0x1C, 0x53, 0xBB, 0x6B]), .. El([0x53, 0xAC], U(3356618, 3))]),
        ]);
        var longueurRecherche = Recherche(0, 0).Length;
        var posInfo = longueurRecherche + vide.Length;
        var recherche = Recherche(posInfo, posInfo + info.Length);
        byte[] contenu = [.. recherche, .. vide, .. info, .. pistes, .. Grappe];
        byte[] ebml = [0x1A, 0x45, 0xDF, 0xA3, 0x84, 0x42, 0x86, 0x81, 0x01];
        var segment = El([0x18, 0x53, 0x80, 0x67], contenu, 8);
        return ([.. ebml, .. segment], ebml.Length + 12);
    }

    [Fact]
    public void La_rotation_du_coeur_se_lit_dans_le_journal_de_lancement_du_bon_jeu()
    {
        string[] journal =
        [
            "[INFO] [Core] Loading dynamic libretro core from: \"E:\\RetroBat\\emulators\\retroarch\\core_proxy\\fbneo_libretro.dll\".",
            "[INFO] [Environ] SET_ROTATION: \"3\" (270 deg).",
            "[INFO] [Core] Geometry: 288x224, Aspect: 0.750, FPS: 60.00, Sample rate: 44100.00 Hz.",
            "[INFO] [Content] CRC32: 0x0b3c1d72.",
        ];
        Assert.Equal(3, RetroBat.Api.Replay.Runtime.ReplayCoreTimingProbe.LireRotation(journal, "0b3c1d72"));
        Assert.Equal(3, RetroBat.Api.Replay.Runtime.ReplayCoreTimingProbe.LireRotation(journal, "B3C1D72"));
        // Sans empreinte du jeu en cours, la rotation du journal vaut.
        Assert.Equal(3, RetroBat.Api.Replay.Runtime.ReplayCoreTimingProbe.LireRotation(journal, null));
        // Le journal d'un autre jeu ne dit rien du jeu en cours.
        Assert.Null(RetroBat.Api.Replay.Runtime.ReplayCoreTimingProbe.LireRotation(journal, "6628aeb0"));
        Assert.Null(RetroBat.Api.Replay.Runtime.ReplayCoreTimingProbe.LireRotation(["[INFO] [Content] CRC32: 0x6628aeb0."], "6628aeb0"));
    }

    [Fact]
    public void Le_sens_de_rotation_suit_celui_annonce_par_le_coeur()
    {
        Assert.Equal(-90, MatroskaRotation.RoulisPour(3));
        Assert.Equal(90, MatroskaRotation.RoulisPour(1));
        Assert.Equal(180, MatroskaRotation.RoulisPour(2));
        Assert.Equal(0, MatroskaRotation.RoulisPour(0));
        Assert.Equal(-90, MatroskaRotation.RoulisPour(-1));
    }

    [Fact]
    public void Un_film_couche_est_redresse_sur_place_sans_que_rien_d_autre_ne_bouge()
    {
        var (fichier, origine) = DebutDeFilm();
        Assert.True(MatroskaEntete.Lire(fichier, fichier.Length)!.Couchee);

        var correction = MatroskaRotation.Corriger(fichier, 3);
        Assert.NotNull(correction);
        var (debut, octets) = correction!.Value;
        var redresse = (byte[])fichier.Clone();
        octets.CopyTo(redresse, debut);

        // Meme longueur, et la grappe d'images n'a pas bouge.
        Assert.Equal(fichier.Length, redresse.Length);
        Assert.Equal(Grappe, redresse[^Grappe.Length..]);
        // Affichage remis dans le sens de l'image : le film n'est plus couche, la rotation porte le reste.
        var entete = MatroskaEntete.Lire(redresse, redresse.Length);
        Assert.Equal(new EnteteVideo(1152, 896, 4, 3), entete);
        Assert.False(entete!.Couchee);
        // La rotation : Projection { ProjectionPoseRoll = -90.0 }.
        var roulis = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(roulis, -90);
        byte[] projection = [0x76, 0x70, 0x8B, 0x76, 0x75, 0x88, .. roulis];
        Assert.True(Chercher(redresse, projection) > 0);
        // Une seconde correction ne fait rien : la rotation est deja la.
        Assert.Null(MatroskaRotation.Corriger(redresse, 3));
        // Les positions de l'en-tete de recherche pointent toujours sur les informations et les pistes.
        // Les identifiants apparaissent aussi dans l'en-tete de recherche : les vrais elements sont les derniers.
        var info = redresse.AsSpan().LastIndexOf(new byte[] { 0x15, 0x49, 0xA9, 0x66 });
        var pistes = redresse.AsSpan().LastIndexOf(new byte[] { 0x16, 0x54, 0xAE, 0x6B });
        Assert.True(info > 0 && pistes > info);
        Assert.True(Chercher(redresse, [0x53, 0xAC, 0x82, .. U(info - origine, 2)]) > 0);
        Assert.True(Chercher(redresse, [0x53, 0xAC, 0x82, .. U(pistes - origine, 2)]) > 0);
    }

    [Fact]
    public void Un_film_droit_ou_sans_reserve_n_est_pas_touche()
    {
        var (fichier, _) = DebutDeFilm();
        Assert.Null(MatroskaRotation.Corriger(fichier, 0));
        // Une somme de controle fausse : on ne touche a rien.
        var abime = (byte[])fichier.Clone();
        var pistes = abime.AsSpan().LastIndexOf(new byte[] { 0x16, 0x54, 0xAE, 0x6B });
        abime[pistes + 20] ^= 0xFF;
        Assert.Null(MatroskaRotation.Corriger(abime, 3));
    }

    /// <summary>
    /// Sur un vrai film, a la demande : NELFE_MKV_A_REDRESSER=chemin d'une COPIE d'un film couche de RetroArch.
    /// Sans la variable, le test ne fait rien.
    /// </summary>
    [Fact]
    public void Un_vrai_film_de_retroarch_se_redresse()
    {
        var chemin = Environment.GetEnvironmentVariable("NELFE_MKV_A_REDRESSER");
        if (string.IsNullOrEmpty(chemin) || !File.Exists(chemin)) return;
        Assert.True(MatroskaEntete.Lire(chemin)!.Couchee);
        Assert.True(MatroskaRotation.Redresser(chemin, 3));
        Assert.False(MatroskaEntete.Lire(chemin)!.Couchee);
    }
}
