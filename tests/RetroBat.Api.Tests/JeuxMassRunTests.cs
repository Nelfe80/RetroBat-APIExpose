using RetroBat.Api.MassRun;
using Xunit;

namespace RetroBat.Api.Tests;

public class JeuxMassRunTests
{
    [Fact]
    public void Sonic_court_sur_son_coeur_hote_avec_ses_dix_neuf_niveaux()
    {
        var sonic = JeuxMassRun.Trouver("megadrive/sonic-the-hedgehog")!;
        Assert.Equal("genesis_plus_gx_ghosts", sonic.Coeur);
        Assert.Equal(0xf9394e97u, sonic.Crc);
        string[] actes = ["green-hill", "marble", "spring-yard", "labyrinth", "star-light", "scrap-brain"];
        var niveaux = actes.SelectMany(a => new[] { a + "-1", a + "-2", a + "-3" }).Append("final-zone").ToArray();
        Assert.Equal(19, niveaux.Length);
        Assert.All(niveaux, n => Assert.Matches(sonic.Niveaux, n));
        Assert.DoesNotMatch(sonic.Niveaux, "1-1");
        Assert.DoesNotMatch(sonic.Niveaux, "green-hill-4");
        Assert.DoesNotMatch(sonic.Niveaux, "../green-hill-1");
        Assert.EndsWith(Path.Combine("megadrive", "sonic-the-hedgehog", "green-hill-2-panneau.state"), JeuxMassRun.CheminDeLEtat(sonic, "green-hill-2", "r"));
    }

    [Fact]
    public void Super_Mario_Bros_reste_le_jeu_d_un_ancien_noeud()
    {
        Assert.Equal("fceumm_ghosts", JeuxMassRun.ParDefaut.Coeur);
        Assert.Same(JeuxMassRun.ParDefaut, JeuxMassRun.Trouver("NES/super-mario-bros"));
        Assert.Null(JeuxMassRun.Trouver("snes/jeu-inconnu"));   // (06/11) super-mario-world est maintenant un jeu de MASS RUN
        Assert.False(JeuxMassRun.FormatMegaDrive("nes/super-mario-bros"));
        Assert.True(JeuxMassRun.FormatMegaDrive("megadrive/sonic-the-hedgehog"));
        Assert.False(JeuxMassRun.FormatAUnSprite("nes/super-mario-bros"));
        Assert.True(JeuxMassRun.FormatAUnSprite("megadrive/sonic-the-hedgehog"));
        Assert.True(JeuxMassRun.FormatAUnSprite("snes/super-metroid"));
    }

    [Fact]
    public void Super_Metroid_court_ses_segments_de_boss_sur_snes9x_ghosts()
    {
        var sm = JeuxMassRun.Trouver("snes/super-metroid")!;
        Assert.Equal("snes9x_ghosts", sm.Coeur);
        Assert.Equal(0xd63ed5f8u, sm.Crc);
        Assert.True(sm.PosesEnImages);
        Assert.Equal(60.098812, sm.ImagesParSeconde, 6);
        Assert.All(new[] { "kraid", "phantoon", "draygon", "ridley", "mother-brain", "ceres" }, n => Assert.Matches(sm.Niveaux, n));
        Assert.DoesNotMatch(sm.Niveaux, "tourian");
        Assert.DoesNotMatch(sm.Niveaux, "../kraid");
        // les etats d'epreuve du Data Pack : snes/super-metroid/kraid.state
        Assert.EndsWith(Path.Combine("snes", "super-metroid", "kraid.state"), JeuxMassRun.CheminDeLEtat(sm, "kraid", "r"));
        // l'arrivee de chaque segment, comme ses reglements
        Assert.Equal((0xD829u, 0x01u), JeuxMassRun.ArriveeDuNiveau(sm, "kraid"));
        Assert.Equal((0xD82Cu, 0x03u), JeuxMassRun.ArriveeDuNiveau(sm, "draygon"));
        Assert.Equal((0x0998u, 0x26u), JeuxMassRun.ArriveeDuNiveau(sm, "mother-brain"));
        Assert.Equal((0x0998u, 0x20u), JeuxMassRun.ArriveeDuNiveau(sm, "ceres"));
        Assert.Null(JeuxMassRun.ArriveeDuNiveau(JeuxMassRun.ParDefaut, "1-1"));
        Assert.Equal("Mother Brain", JeuxMassRun.NomDuNiveau("mother-brain"));
    }

    /// <summary>Une pose SNES : une image de 64 x 64 (index de palette), tassee a deux pixels par octet.</summary>
    private static byte[] PoseTassee(int graine)
    {
        var r = new Random(graine);
        var t = new byte[MassRunProtocole.PoseTassee];
        for (var i = 600; i < 1400; i++) t[i] = (byte)r.Next(256);
        return t;
    }

    [Fact]
    public void L_empreinte_d_une_pose_est_celle_du_coeur_sur_l_image_detassee()
    {
        var t = PoseTassee(1);
        var image = new byte[MassRunProtocole.PoseTassee * 2];
        for (var i = 0; i < t.Length; i++) { image[2 * i] = (byte)(t[i] >> 4); image[2 * i + 1] = (byte)(t[i] & 15); }
        Assert.Equal(JeuxMassRun.Crc32(image) & 0xFFFFFFu, MassRunProtocole.EmpreinteDeLaPose(t));
        Assert.InRange(MassRunProtocole.EmpreinteDeLaPose(t), 1u, 0xFFFFFFu);
    }

    [Fact]
    public void Les_poses_en_images_voyagent_compressees_et_une_fausse_empreinte_est_ecartee()
    {
        var a = PoseTassee(2);
        var b = PoseTassee(3);
        var ea = MassRunProtocole.EmpreinteDeLaPose(a);
        var eb = MassRunProtocole.EmpreinteDeLaPose(b);
        var paquet = MassRunProtocole.PosesEnPaquet(7, [(ea, a), (eb ^ 1, b)]);
        Assert.True(paquet.Length < 2 * MassRunProtocole.PoseTassee);   // compresse
        var lues = MassRunProtocole.PosesDuPaquet(paquet, out var place);
        Assert.Equal(7, place);
        var seule = Assert.Single(lues);            // b est annoncee sous une autre empreinte : ecartee
        Assert.Equal(ea, seule.Empreinte);
        Assert.Equal(a, seule.Tassee);
        Assert.Throws<InvalidDataException>(() => MassRunProtocole.PosesDuPaquet(paquet.AsSpan(0, paquet.Length - 3), out _));
        // le sceau d'un corps depend de la cle de la place
        var cle = new byte[32];
        cle[0] = 1;
        Assert.Equal(8, MassRunProtocole.SceauDuCorps(paquet, cle).Length);
        Assert.NotEqual(MassRunProtocole.SceauDuCorps(paquet, cle), MassRunProtocole.SceauDuCorps(paquet, new byte[32]));
    }

    [Fact]
    public void Une_image_de_piste_snes_porte_l_empreinte_de_sa_pose_aux_octets_5_9_et_10()
    {
        var im = new byte[MassRunProtocole.ImageDePiste];
        Assert.Equal(0u, JeuxMassRun.EmpreinteDeLaPose(im));      // rien a dessiner
        im[6] = 1; im[9] = 0x56; im[10] = 0x34; im[5] = 0x12;
        im[2] = 0x9F; im[3] = 0xA5;                                // la salle de Kraid
        JeuxMassRun.PlacerMegaDrive(im, 300, 0x18B);
        Assert.Equal(0x123456u, JeuxMassRun.EmpreinteDeLaPose(im));
        // le dictionnaire du noeud numerote la pose (octets 9 et 10) ; l'octet 5 passe par l'entree compacte (« controle »)
        var pose = MassRunProtocole.Pose(im);
        var entree = new byte[MassRunProtocole.EntreeCompacte];
        im.AsSpan(0, 2).CopyTo(entree.AsSpan(3));
        im.AsSpan(2, 3).CopyTo(entree.AsSpan(5));
        entree[8] = im[5];
        entree[9] = im[11];
        entree[10] = im[8];
        var refaite = new byte[MassRunProtocole.ImageDePiste];
        MassRunProtocole.Refaire(entree, pose, refaite);
        Assert.Equal(0x123456u, JeuxMassRun.EmpreinteDeLaPose(refaite));
        Assert.Equal(300, JeuxMassRun.XMegaDrive(refaite));
        Assert.Equal(0x18B, JeuxMassRun.YMegaDrive(refaite));
        Assert.Equal(MassRunProtocole.Scene(im), MassRunProtocole.Scene(refaite));
        // entre deux positions : celle de la plus proche garde son empreinte
        var b = (byte[])im.Clone();
        b[9] = 0x77;
        JeuxMassRun.PlacerMegaDrive(b, 320, 0x18B);
        var sortie = new byte[MassRunProtocole.ImageDePiste];
        ClientDeSalle.GlisserMegaDrive(im, b, 0.25, sortie);
        Assert.Equal(305, JeuxMassRun.XMegaDrive(sortie));
        Assert.Equal(0x123456u, JeuxMassRun.EmpreinteDeLaPose(sortie));
    }

    [Fact]
    public void Les_poses_snes_du_Data_Pack_vont_dans_le_dossier_systeme()
    {
        var racine = Path.Combine(Path.GetTempPath(), "nelfe-snposes-" + Guid.NewGuid().ToString("N"));
        try
        {
            var etats = Path.Combine(racine, "etats");
            var systeme = Path.Combine(racine, "bios");
            var sm = JeuxMassRun.Trouver("snes/super-metroid")!;
            Directory.CreateDirectory(Path.Combine(etats, "snes", "super-metroid"));
            File.WriteAllBytes(Path.Combine(etats, "snes", "super-metroid", "poses.mdposes"), [9]);
            Assert.False(FichiersDesCoeursHotes.PoserLesPoses(sm, etats, systeme));   // pas le format de la SNES
            File.WriteAllBytes(Path.Combine(etats, "snes", "super-metroid", "poses.snposes"), [4, 5]);
            Assert.True(FichiersDesCoeursHotes.PoserLesPoses(sm, etats, systeme));
            Assert.Equal([4, 5], File.ReadAllBytes(Path.Combine(systeme, "nelfeplay", "massrun", "d63ed5f8", "poses-datapack.snposes")));
        }
        finally
        {
            Directory.Delete(racine, true);
        }
    }

    [Theory]
    [InlineData("1-1", "1-1")]
    [InlineData("green-hill-2", "Green Hill 2")]
    [InlineData("final-zone", "Final Zone")]
    [InlineData("", "")]
    [InlineData("1-f", "1-F")]
    [InlineData("yi2", "Yoshi's Island 2")]
    [InlineData("ds1", "Donut Secret 1")]
    [InlineData("air-man", "Air Man")]
    [InlineData("stage-3", "Stage 3")]
    public void Le_niveau_se_dit_en_clair(string niveau, string attendu) => Assert.Equal(attendu, JeuxMassRun.NomDuNiveau(niveau));

    [Fact]
    public void Les_profils_et_les_poses_du_Data_Pack_vont_dans_le_dossier_systeme()
    {
        var racine = Path.Combine(Path.GetTempPath(), "nelfe-coeurs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var profils = Path.Combine(racine, "profils");
            var etats = Path.Combine(racine, "etats");
            var systeme = Path.Combine(racine, "bios");
            Directory.CreateDirectory(profils);
            File.WriteAllText(Path.Combine(profils, "f9394e97.ini"), "[profil]\nmode=0xF600\n");
            File.WriteAllText(Path.Combine(profils, "../x.ini"), "hors du dossier");
            File.WriteAllText(Path.Combine(profils, "pas-un-crc.ini"), "ignore");
            Assert.Equal(1, FichiersDesCoeursHotes.PoserLesProfils(profils, systeme));
            Assert.Equal(0, FichiersDesCoeursHotes.PoserLesProfils(profils, systeme));   // identique : pas recopie
            Assert.True(File.Exists(Path.Combine(systeme, "nelfeplay", "profils", "f9394e97.ini")));
            Assert.False(File.Exists(Path.Combine(systeme, "nelfeplay", "profils", "pas-un-crc.ini")));

            var sonic = JeuxMassRun.Trouver("megadrive/sonic-the-hedgehog")!;
            Assert.False(FichiersDesCoeursHotes.PoserLesPoses(sonic, etats, systeme));
            Directory.CreateDirectory(Path.Combine(etats, "megadrive", "sonic-the-hedgehog"));
            File.WriteAllBytes(Path.Combine(etats, "megadrive", "sonic-the-hedgehog", "poses.mdposes"), [1, 2, 3]);
            Assert.True(FichiersDesCoeursHotes.PoserLesPoses(sonic, etats, systeme));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(systeme, "nelfeplay", "massrun", "f9394e97", "poses-datapack.mdposes")));
        }
        finally
        {
            Directory.Delete(racine, true);
        }
    }

    [Fact]
    public void Le_retard_d_un_coureur_suit_ses_positions_les_plus_en_retard()
    {
        var c = new ClientDeSalle.Coureur();
        // une position toutes les 4 images, recue 3 images apres son instant : montre 5 images dans le passe
        for (var l = 100.0; l < 200; l += 4) ClientDeSalle.NoterLEcart(c, l - 3, l);
        Assert.Equal(-5, c.Decalage!.Value, 3);
        // une bouffee de retard (20 images) : le decalage recule d'au plus une image par position
        ClientDeSalle.NoterLEcart(c, 204 - 20, 204);
        Assert.Equal(-6, c.Decalage!.Value, 3);
        for (var l = 208.0; l < 260; l += 4) ClientDeSalle.NoterLEcart(c, l - 3, l);
        Assert.Equal(-19, c.Decalage!.Value, 3);
        Assert.True(c.Decalage <= -19);
        // la bouffee sortie de la fenetre de 2 s, il revient doucement (0,25 image par position)
        for (var l = 260.0; l < 340; l += 4) ClientDeSalle.NoterLEcart(c, l - 3, l);
        var avant = c.Decalage!.Value;
        ClientDeSalle.NoterLEcart(c, 340 - 3, 340);
        Assert.Equal(avant + 0.25, c.Decalage!.Value, 3);
    }

    [Fact]
    public void L_image_de_salle_d_attente_est_commune_et_reste_au_dessus_des_images_de_course()
    {
        var go = new DateTime(2026, 11, 7, 21, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1_000_000u, ClientDeSalle.ImageDAttente(go, go, 60.0988));
        Assert.Equal(1_000_000u - 601u, ClientDeSalle.ImageDAttente(go.AddSeconds(-10), go, 60.0988));
        Assert.Equal(1_000_060u, ClientDeSalle.ImageDAttente(go.AddSeconds(1), go, 60.0988));
        // deux bornes a 50 ms d'ecart d'horloge : 3 images, dans l'octet du noeud
        Assert.InRange(ClientDeSalle.ImageDAttente(go.AddSeconds(-30), go, 60.0988) - ClientDeSalle.ImageDAttente(go.AddSeconds(-30.05), go, 60.0988), 2u, 4u);
    }

    [Fact]
    public void En_salle_d_attente_un_coureur_reste_entre_deux_positions_recues()
    {
        // des positions toutes les 4 images environ, datees a leur arrivee, avec des retards de 0 a 5 images (07/11)
        var c = new ClientDeSalle.Coureur();
        var hasard = new Random(7);
        var arrivees = new List<double>();
        for (var k = 0; k < 120; k++) arrivees.Add(k * 3.85 + hasard.Next(0, 6));
        arrivees.Sort();
        var prochaine = 0;
        var depasse = 0;
        for (var maintenant = 0.0; maintenant < arrivees[^1]; maintenant += 1)
        {
            while (prochaine < arrivees.Count && arrivees[prochaine] <= maintenant)
            {
                var t = arrivees[prochaine++];
                if (c.Echantillons.Count == 0 || t > c.Echantillons[^1].T)
                {
                    c.Echantillons.Add((t, new byte[1]));
                    if (c.Echantillons.Count > 24) c.Echantillons.RemoveAt(0);
                    ClientDeSalle.NoterLAttente(c);
                }
            }
            if (maintenant > 60 && maintenant + c.Decalage!.Value > c.Echantillons[^1].T) depasse++;
        }
        Assert.Equal(0, depasse);
        Assert.InRange(-c.Decalage!.Value, ClientDeSalle.RetardMinimalDAttente, ClientDeSalle.RetardMaximalDAttente);
    }

    [Fact]
    public void Le_contenu_d_une_rom_est_le_crc_du_fichier_ou_de_l_unique_jeu_de_son_archive()
    {
        Assert.Equal(0xCBF43926u, JeuxMassRun.Crc32("123456789"u8));
        var dossier = Path.Combine(Path.GetTempPath(), "nelfe-rom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            var brut = Path.Combine(dossier, "jeu.md");
            File.WriteAllBytes(brut, "123456789"u8.ToArray());
            Assert.Equal(0xCBF43926u, JeuxMassRun.ContenuDe(brut));
            var zip = Path.Combine(dossier, "jeu.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            {
                using var s = archive.CreateEntry("jeu.nes").Open();
                s.Write("123456789"u8);
            }
            Assert.Equal(0xCBF43926u, JeuxMassRun.ContenuDe(zip));
            Assert.Null(JeuxMassRun.ContenuDe(Path.Combine(dossier, "absent.zip")));
        }
        finally
        {
            Directory.Delete(dossier, true);
        }
    }

    [Fact]
    public void Le_billet_de_coureur_dit_la_salle_le_jeu_l_appareil_et_le_coeur()
    {
        var quand = new DateTime(2026, 11, 5, 13, 48, 15, DateTimeKind.Utc);
        var corps = BilletDeCoureur.Corps(140651, "nes/super-mario-bros", "4-1", "NELFE80", "d1", "k1", "c1", quand);
        Assert.Equal(BilletDeCoureur.Genre, (string?)corps["kind"]);
        Assert.Equal(140651u, (uint)corps["room"]!);
        Assert.Equal("2026-11-05T13:48:15Z", (string?)corps["signed_at"]);
        // la signature se verifie comme celle d'un passeport : ECDSA P-256 sur le JCS du corps
        using var ecdsa = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var signature = RetroBat.Api.Scoring.Crypto.SignB64Url(ecdsa, RetroBat.Api.Scoring.Jcs.CanonicalBytes(corps));
        Assert.True(RetroBat.Api.Scoring.Crypto.Verify(ecdsa.ExportSubjectPublicKeyInfo(), RetroBat.Api.Scoring.Jcs.CanonicalBytes(corps), signature));
        Assert.Null(BilletDeCoureur.Signer(null, 1, "j", "n", "p", null));
    }

    private static byte[] Image(int x, int y, byte zone, byte acte, byte pose, byte drapeaux)
    {
        var im = new byte[MassRunProtocole.ImageDePiste];
        im[2] = zone; im[3] = acte; im[6] = 1; im[9] = pose; im[10] = drapeaux;
        JeuxMassRun.PlacerMegaDrive(im, x, y);
        return im;
    }

    [Fact]
    public void Un_coureur_mega_drive_glisse_sur_x_et_sur_y_en_deux_octets()
    {
        var a = Image(1000, 0x2F0, 0, 1, 5, 0);
        var b = Image(1040, 0x310, 0, 1, 7, 1);
        var sortie = new byte[MassRunProtocole.ImageDePiste];
        ClientDeSalle.GlisserMegaDrive(a, b, 0.25, sortie);
        Assert.Equal(1010, JeuxMassRun.XMegaDrive(sortie));
        Assert.Equal(0x2F8, JeuxMassRun.YMegaDrive(sortie));
        Assert.Equal(5, sortie[9]);      // la pose de la plus proche
        ClientDeSalle.GlisserMegaDrive(a, b, 0.75, sortie);
        Assert.Equal(7, sortie[9]);
        Assert.Equal(1, sortie[10]);
        Assert.Equal(1030, JeuxMassRun.XMegaDrive(sortie));
    }

    [Fact]
    public void Un_saut_ou_un_autre_acte_ne_glisse_pas()
    {
        var sortie = new byte[MassRunProtocole.ImageDePiste];
        ClientDeSalle.GlisserMegaDrive(Image(5000, 300, 0, 1, 5, 0), Image(80, 300, 0, 1, 5, 0), 0.4, sortie);
        Assert.Equal(5000, JeuxMassRun.XMegaDrive(sortie));
        ClientDeSalle.GlisserMegaDrive(Image(100, 300, 0, 1, 5, 0), Image(110, 300, 0, 2, 5, 0), 0.4, sortie);
        Assert.Equal(100, JeuxMassRun.XMegaDrive(sortie));
    }

    [Fact]
    public void La_pose_mega_drive_passe_par_le_dictionnaire_du_noeud_sans_rien_perdre()
    {
        var image = Image(0x1234, 0x0567, 3, 2, 0x2A, 1);
        var pose = MassRunProtocole.Pose(image);
        // l'entree compacte du noeud : camera, scene, controle, x et y du premier sprite
        var entree = new byte[MassRunProtocole.EntreeCompacte];
        image.AsSpan(0, 2).CopyTo(entree.AsSpan(3));
        image.AsSpan(2, 3).CopyTo(entree.AsSpan(5));
        entree[8] = image[5];
        entree[9] = image[11];
        entree[10] = image[8];
        var refaite = new byte[MassRunProtocole.ImageDePiste];
        MassRunProtocole.Refaire(entree, pose, refaite);
        Assert.Equal(0x1234, JeuxMassRun.XMegaDrive(refaite));
        Assert.Equal(0x0567, JeuxMassRun.YMegaDrive(refaite));
        Assert.Equal(new byte[] { 3, 2 }, refaite[2..4]);
        Assert.Equal(0x2A, refaite[9]);
        Assert.Equal(1, refaite[10]);
    }

    [Theory]
    [InlineData("nes/super-mario-bros-3", "fceumm_ghosts", 0x0b742b33u, "1-f", "1-7", "1-1-entree.state")]
    [InlineData("nes/mega-man-2", "fceumm_ghosts", 0xa9bd44bcu, "air-man", "dr-wily-1", "air-man-entree.state")]
    [InlineData("snes/mega-man-x", "snes9x_ghosts", 0xded53c64u, "chill-penguin", "sigma", "chill-penguin-entree.state")]
    [InlineData("megadrive/rocket-knight-adventures", "genesis_plus_gx_ghosts", 0xa6efec47u, "stage-7", "stage-2", "stage-1-panneau.state")]
    [InlineData("megadrive/sonic-the-hedgehog-2", "genesis_plus_gx_ghosts", 0x24ab4c3au, "death-egg", "wing-fortress", "emerald-hill-1-panneau.state")]
    [InlineData("snes/super-mario-world", "snes9x_ghosts", 0xb19ed489u, "ds1", "dp2", "yi1-entree.state")]
    public void Les_jeux_ouverts_en_speedrun_ont_leur_salle(string id, string coeur, uint crc, string admis, string refuse, string etat)
    {
        var jeu = JeuxMassRun.Trouver(id)!;
        Assert.Equal(coeur, jeu.Coeur);
        Assert.Equal(crc, jeu.Crc);
        Assert.Matches(jeu.Niveaux, admis);
        Assert.DoesNotMatch(jeu.Niveaux, refuse);
        var niveauDeLEtat = etat[..etat.IndexOf(jeu.SuffixeDeLEtat + ".state", StringComparison.Ordinal)];
        Assert.EndsWith(Path.Combine(jeu.Systeme, jeu.RomGroup, etat), JeuxMassRun.CheminDeLEtat(jeu, niveauDeLEtat, "r"));
        Assert.Equal(jeu.Systeme == "snes", jeu.PosesEnImages);
    }

    [Fact]
    public void Un_autre_dump_de_la_meme_rom_est_admis()
    {
        var smb3 = JeuxMassRun.Trouver("nes/super-mario-bros-3")!;
        Assert.True(smb3.Admet(0x0b742b33));
        Assert.True(smb3.Admet(0xe8c3af69));   // No-Intro, en-tete NES 2.0
        Assert.False(smb3.Admet(0x12345678));
        Assert.False(smb3.Admet(null));
        Assert.True(JeuxMassRun.Trouver("nes/mega-man-2")!.Admet(0x80e08660));
        Assert.False(JeuxMassRun.ParDefaut.Admet(0xe8c3af69));
    }

    [Fact]
    public void Rka_et_Mega_Man_X_donnent_leur_arrivee_au_coeur()
    {
        var rka = JeuxMassRun.Trouver("megadrive/rocket-knight-adventures")!;
        Assert.Equal((0xFB13u, 0x05u), JeuxMassRun.ArriveeDuNiveau(rka, "stage-1"));
        Assert.Equal((0xFB13u, 0x2Eu), JeuxMassRun.ArriveeDuNiveau(rka, "stage-3"));
        Assert.Equal((0xFB13u, 0x30u), JeuxMassRun.ArriveeDuNiveau(rka, "stage-4"));
        Assert.Equal((0xFB13u, 0x32u), JeuxMassRun.ArriveeDuNiveau(rka, "stage-7"));
        Assert.Equal((0x0E68u, 0xFFu), JeuxMassRun.ArriveeDuNiveau(JeuxMassRun.Trouver("snes/mega-man-x")!, "storm-eagle"));
        Assert.Null(JeuxMassRun.ArriveeDuNiveau(JeuxMassRun.Trouver("nes/mega-man-2")!, "air-man"));
        var smb3 = JeuxMassRun.Trouver("nes/super-mario-bros-3")!;
        Assert.Equal((0x05F3u, 0x81u), JeuxMassRun.ArriveeDuNiveau(smb3, "1-4"));
        Assert.Equal((0x04E4u, 0x04u), JeuxMassRun.ArriveeDuNiveau(smb3, "1-f"));
    }
}
