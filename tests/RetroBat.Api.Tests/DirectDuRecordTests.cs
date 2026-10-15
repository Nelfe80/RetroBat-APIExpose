using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Le direct d'un record : lecture du film qui s'ecrit, instants des images, RTMP, regle de declenchement.</summary>
public sealed class DirectDuRecordTests
{
    // Un element EBML : identifiant, taille sur un octet (ou huit), donnees.
    private static byte[] E(byte[] id, byte[] donnees)
    {
        if (donnees.Length < 127) return [.. id, (byte)(0x80 | donnees.Length), .. donnees];
        var taille = new byte[8];
        taille[0] = 0x01;
        for (var i = 0; i < 7; i++) taille[7 - i] = (byte)((long)donnees.Length >> (8 * i));
        return [.. id, .. taille, .. donnees];
    }

    private static byte[] Entier2(int v) => [(byte)(v >> 8), (byte)v];

    // Un en-tete de film comme RetroArch l'ecrit : segment de taille inconnue, echelle 1 ms, une piste video H.264 et une
    // piste de son AAC.
    private static byte[] Entete()
    {
        var info = E([0x15, 0x49, 0xA9, 0x66], E([0x2A, 0xD7, 0xB1], [0x0F, 0x42, 0x40]));
        var video = E([0xAE], [.. E([0xD7], [1]), .. E([0x83], [1]), .. E([0x86], "V_MPEG4/ISO/AVC"u8.ToArray()),
            .. E([0x63, 0xA2], [1, 2, 3, 4]), .. E([0xE0], [.. E([0xB0], Entier2(1024)), .. E([0xBA], Entier2(896))])]);
        var son = E([0xAE], [.. E([0xD7], [2]), .. E([0x83], [2]), .. E([0x86], "A_AAC"u8.ToArray()), .. E([0x63, 0xA2], [0x12, 0x10]),
            .. E([0xE1], E([0xB5], [0x47, 0x3B, 0x80, 0x00]))]);
        byte[] ebml = [0x1A, 0x45, 0xDF, 0xA3, 0x84, 0x42, 0x86, 0x81, 0x01];
        byte[] segment = [0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        return [.. ebml, .. segment, .. info, .. E([0x16, 0x54, 0xAE, 0x6B], [.. video, .. son])];
    }

    // Une grappe : son instant, puis des blocs simples (piste, instant relatif, image cle).
    private static byte[] Grappe(int temps, params (int Piste, short Relatif, bool Cle, byte Octet)[] blocs)
    {
        var corps = new List<byte>(E([0xE7], Entier2(temps)));
        foreach (var b in blocs)
            corps.AddRange(E([0xA3], [(byte)(0x80 | b.Piste), (byte)(b.Relatif >> 8), (byte)b.Relatif, (byte)(b.Cle ? 0x80 : 0), b.Octet, b.Octet]));
        return E([0x1F, 0x43, 0xB6, 0x75], corps.ToArray());
    }

    [Fact]
    public void Le_film_qui_s_ecrit_se_lit_grappe_par_grappe()
    {
        var chemin = Path.Combine(Path.GetTempPath(), "nelfe-direct-" + Guid.NewGuid().ToString("N") + ".mkv");
        try
        {
            var g1 = Grappe(0, (1, 0, true, 0xA1), (2, 0, true, 0xB1), (1, 50, false, 0xA2), (1, 17, false, 0xA3));
            File.WriteAllBytes(chemin, [.. Entete(), .. g1]);
            var film = new FilmMkv(chemin);
            Assert.True(film.LireEntete());
            Assert.Equal("V_MPEG4/ISO/AVC", film.Video!.Codec);
            Assert.Equal((1024, 896), (film.Video.Largeur, film.Video.Hauteur));
            Assert.Equal([1, 2, 3, 4], film.Video.Prive);
            Assert.Equal(48000, film.Son!.Frequence);

            var lues = film.NouvellesGrappes();
            Assert.Single(lues);
            Assert.Equal(4, lues[0].Count);
            Assert.True(lues[0][0].Cle);
            Assert.Equal(50, lues[0][2].TempsMs);

            // Rien de neuf : rien a lire. Une grappe a moitie ecrite attend d'etre complete.
            Assert.Empty(film.NouvellesGrappes());
            var g2 = Grappe(4170, (1, 0, true, 0xC1));
            using (var f = new FileStream(chemin, FileMode.Append)) f.Write(g2, 0, g2.Length - 3);
            Assert.Empty(film.NouvellesGrappes());
            using (var f = new FileStream(chemin, FileMode.Append)) f.Write(g2, g2.Length - 3, 3);
            var suite = film.NouvellesGrappes();
            Assert.Single(suite);
            Assert.Equal(4170, suite[0][0].TempsMs);
        }
        finally
        {
            File.Delete(chemin);
        }
    }

    [Fact]
    public void Le_direct_part_de_la_grappe_suivante()
    {
        var chemin = Path.Combine(Path.GetTempPath(), "nelfe-direct-" + Guid.NewGuid().ToString("N") + ".mkv");
        try
        {
            File.WriteAllBytes(chemin, [.. Entete(), .. Grappe(0, (1, 0, true, 1)), .. Grappe(4170, (1, 0, true, 2))]);
            var film = new FilmMkv(chemin);
            Assert.True(film.LireEntete());
            film.AllerALaFin();
            Assert.Empty(film.NouvellesGrappes());
            var g3 = Grappe(8340, (1, 0, true, 3));
            using (var f = new FileStream(chemin, FileMode.Append)) f.Write(g3);
            Assert.Equal(8340, Assert.Single(Assert.Single(film.NouvellesGrappes())).TempsMs);
        }
        finally
        {
            File.Delete(chemin);
        }
    }

    [Fact]
    public void L_instant_de_decodage_des_images_se_deduit_de_leurs_instants_d_affichage()
    {
        // Ordre de decodage d'un groupe x264 avec images B : I(0) P(50) B(17) B(33), et du son.
        var grappe = new List<FilmMkv.Bloc>
        {
            new(1, 0, true, [1]), new(2, 0, true, [9]), new(1, 50, false, [2]), new(1, 17, false, [3]), new(1, 33, false, [4]),
            new(2, 21, true, [8]),
        };
        var etiquettes = RelaisDuFilm.Etiquettes(grappe, 1);
        var images = etiquettes.Where(e => e.Video).ToList();
        // L'ordre de decodage est garde ; les instants de decodage croissent et ne depassent jamais l'affichage.
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, images.Select(e => e.Donnees[0]).ToArray());
        Assert.Equal(new[] { -100.0, -83, -67, -50 }, images.Select(e => e.Dts).ToArray());
        Assert.All(images, e => Assert.True(e.Pts - e.Dts >= 0));
        // Tout part dans l'ordre des instants de decodage.
        Assert.Equal(etiquettes.Select(e => e.Dts).OrderBy(d => d), etiquettes.Select(e => e.Dts));
    }

    [Fact]
    public void Un_message_rtmp_se_decoupe_en_morceaux()
    {
        var charge = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        var m = EmetteurRtmp.Morceaux(6, 9, 1, 1000, charge, 128);
        // En-tete complet (12 octets), 128 octets, puis deux suites d'un octet d'en-tete.
        Assert.Equal(12 + 300 + 2, m.Length);
        Assert.Equal(6, m[0]);
        Assert.Equal(new byte[] { 0, 0x03, 0xE8 }, m[1..4]);
        Assert.Equal(new byte[] { 0, 0x01, 0x2C }, m[4..7]);
        Assert.Equal(9, m[7]);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, m[8..12]);
        Assert.Equal(0xC6, m[12 + 128]);
        Assert.Equal(0xC6, m[12 + 128 + 1 + 128]);

        // Un instant au-dela de 24 bits passe en instant etendu, redit a chaque suite.
        var long_ = EmetteurRtmp.Morceaux(4, 8, 1, 0x1000000, new byte[200], 128);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF }, long_[1..4]);
        Assert.Equal(12 + 4 + 200 + 1 + 4, long_.Length);
    }

    [Fact]
    public void Les_commandes_amf0_se_relisent()
    {
        var flux = new MemoryStream();
        EmetteurRtmp.Amf.Ecrire(flux, "_result");
        EmetteurRtmp.Amf.Ecrire(flux, 4.0);
        EmetteurRtmp.Amf.Ecrire(flux, null);
        EmetteurRtmp.Amf.Ecrire(flux, new Dictionary<string, object?> { ["code"] = "NetStream.Publish.Start", ["ok"] = true });
        var lu = EmetteurRtmp.Amf.LireTout(flux.ToArray());
        Assert.Equal("_result", lu[0]);
        Assert.Equal(4.0, lu[1]);
        Assert.Null(lu[2]);
        var o = Assert.IsType<Dictionary<string, object?>>(lu[3]);
        Assert.Equal("NetStream.Publish.Start", o["code"]);
        Assert.Equal(true, o["ok"]);
    }

    [Theory]
    [InlineData(100L, 101L, 1, false, false, true)]
    [InlineData(100L, 100L, 1, false, false, false)]   // egaler ne suffit pas
    [InlineData(100L, 500L, 2, false, false, false)]   // le joueur 2 d'une partie a deux
    [InlineData(100L, 500L, 1, true, false, false)]    // une fois par partie
    [InlineData(100L, 500L, 1, false, true, false)]    // apres un continue, la partie ne compte plus
    [InlineData(null, 500L, 1, false, false, false)]   // classement inconnu
    public void Le_direct_s_ouvre_au_depassement_du_premier_rang(long? aBattre, long score, int joueur, bool deja, bool fini, bool attendu)
        => Assert.Equal(attendu, DirectDuRecordService.PasseDevant(aBattre, score, joueur, deja, fini));

    [Fact]
    public void Un_jeu_a_modes_n_a_pas_de_direct()
    {
        Assert.True(DirectDuRecordService.SansModes(["1cc", "1lc"]));
        Assert.True(DirectDuRecordService.SansModes(["1cc", "1lc", "1cc-multi"]));
        Assert.False(DirectDuRecordService.SansModes(["1cc", "1cc-original", "1lc"]));
    }

    [Theory]
    [InlineData("exe", true, 3, "exe")]       // l'exe redresse le jeu vertical
    [InlineData("exe", true, 0, "exe")]
    [InlineData(null, true, 0, "relais")]     // sans l'exe, la recopie telle quelle
    [InlineData(null, true, null, "relais")]
    [InlineData(null, true, 3, null)]         // ... qui ne redresse rien : pas de direct pour un jeu couche
    [InlineData(null, true, 1, null)]
    [InlineData("exe", false, 0, "relais")]   // l'exe coupe par la configuration
    [InlineData("exe", false, 1, null)]
    public void L_envoi_passe_par_l_exe_du_direct_s_il_est_la(string? exe, bool permis, int? rotation, string? attendu)
        => Assert.Equal(attendu, DirectDuRecordService.Envoi(exe, permis, rotation));

    [Theory]
    [InlineData("exe", true, 0, "exe")]       // l'exe agrandit le film a la taille d'origine
    [InlineData(null, true, 0, null)]         // la recopie telle quelle ne sait pas : pas de direct
    [InlineData("exe", false, 0, null)]
    public void Un_film_a_la_taille_d_origine_ne_passe_que_par_l_exe(string? exe, bool permis, int? rotation, string? attendu)
        => Assert.Equal(attendu, DirectDuRecordService.Envoi(exe, permis, rotation, origine: true));

    [Fact]
    public void L_adresse_du_direct_ne_passe_jamais_par_la_ligne_de_commande()
    {
        var a = LanceurDuDirect.Arguments(@"C:\captures\fm_x.mkv", 3);
        Assert.Equal(["--film", @"C:\captures\fm_x.mkv", "--adresse-stdin", "--rotation", "3", "--suivre"], a);
        Assert.DoesNotContain(a, x => x.Contains("rtmp", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Essai reel, ignore sans NELFE_DIRECT_FILM ni l'exe du direct a la racine d'APIExpose (NELFE_DIRECT_EXE) : le
    /// lanceur demarre le vrai exe sur un film de RetroArch, lui passe l'adresse du MediaMTX local par l'entree standard,
    /// puis la « partie » s'arrete au bout de 12 s ; l'exe doit finir proprement, sans jamais montrer l'adresse.
    /// </summary>
    [Fact]
    public async Task Le_vrai_exe_du_direct_envoie_un_film_au_serveur_local()
    {
        var film = Environment.GetEnvironmentVariable("NELFE_DIRECT_FILM");
        var exe = Environment.GetEnvironmentVariable("NELFE_DIRECT_EXE");
        if (string.IsNullOrEmpty(film) || !File.Exists(film) || string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
        var lignes = new List<string>();
        var journal = new JournalEnMemoire(lignes);
        const string adresse = "rtmp://127.0.0.1:1935/live/lanceur";
        using var partie = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var r = await LanceurDuDirect.DiffuserAsync(exe, film, adresse, 3, journal, partie.Token);
        Assert.Contains(lignes, l => l.Contains("publication acceptee", StringComparison.Ordinal));
        Assert.DoesNotContain(lignes, l => l.Contains("rtmp://", StringComparison.Ordinal));
        Assert.NotEqual(-1, r.Code);
    }

    private sealed class JournalEnMemoire(List<string> lignes) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (lignes) lignes.Add(formatter(state, exception));
        }
    }

    [Fact]
    public void Une_ligne_de_l_exe_ne_montre_jamais_l_adresse()
    {
        const string adresse = "rtmp://a.rtmp.youtube.com/live2/cle-secrete";
        Assert.Equal("echec : connexion a <adresse> refusee", LanceurDuDirect.Masquer("echec : connexion a " + adresse + " refusee", adresse));
        Assert.Equal("publication acceptee", LanceurDuDirect.Masquer("publication acceptee", adresse));
    }

    /// <summary>
    /// Essai reel, ignore sans NELFE_DIRECT_FILM : recopie dix secondes d'un vrai film de RetroArch vers le MediaMTX local
    /// (rtmp://127.0.0.1:1935/live/csharp), qui doit l'accepter.
    /// </summary>
    [Fact]
    public async Task Un_vrai_film_se_recopie_vers_le_serveur_local()
    {
        var film = Environment.GetEnvironmentVariable("NELFE_DIRECT_FILM");
        if (string.IsNullOrEmpty(film) || !File.Exists(film)) return;
        // Le film est fini : on le relit depuis le debut, comme s'il s'ecrivait.
        var copie = Path.Combine(Path.GetTempPath(), "nelfe-direct-essai.mkv");
        File.Copy(film, copie, overwrite: true);
        try
        {
            using var dix = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var rapport = await RelaisDuFilm.RecopierAsync(copie, "rtmp://127.0.0.1:1935/live/csharp", NullLogger.Instance, dix.Token,
                depuisLeDebut: true);
            Assert.True(rapport.Images > 300, "images envoyees : " + rapport.Images + " (" + rapport.Fin + ")");
        }
        finally
        {
            File.Delete(copie);
        }
    }
}
