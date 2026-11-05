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
