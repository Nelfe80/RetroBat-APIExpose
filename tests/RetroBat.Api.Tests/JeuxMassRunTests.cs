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
        Assert.Null(JeuxMassRun.Trouver("snes/super-metroid"));
        Assert.False(JeuxMassRun.FormatMegaDrive("nes/super-mario-bros"));
        Assert.True(JeuxMassRun.FormatMegaDrive("megadrive/sonic-the-hedgehog"));
    }

    [Theory]
    [InlineData("1-1", "1-1")]
    [InlineData("green-hill-2", "Green Hill 2")]
    [InlineData("final-zone", "Final Zone")]
    [InlineData("", "")]
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
}
