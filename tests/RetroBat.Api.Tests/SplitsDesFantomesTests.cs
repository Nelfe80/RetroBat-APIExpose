using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES SPLITS DES FANTOMES (11/11, lot L5 du CDC Player Deck Patterns) : le fichier du coeur hote, l'ecart au fantome segment
/// par segment, et la garde des splits d'un fantome avec son .nghost. Les valeurs viennent du banc du 1-1 (record rp_06GTST28
/// rejoue par le joueur, fantome d'AshTaste92) : le joueur et le fantome du meme run ont les memes splits a l'image pres.
/// </summary>
public class SplitsDesFantomesTests
{
    private const string Joueur = "NSPLITS1 512\nrun 1\n00000040:0 0\n00000040:1 215\n00000080:0 565\n00000040:5 961\n00000040:6 1389\n";
    private const string Ash = "NSPLITS1 512\n00000040:0 0\n00000040:1 211\n00000040:2 416\n00000040:3 621\n00000040:4 857\n00000040:5 1140\n00000040:6 1544\n";

    [Fact]
    public void Le_fichier_du_coeur_se_lit()
    {
        var j = SplitsDesFantomes.Lire(Joueur)!;
        Assert.Equal(1, j.Run);
        Assert.Equal(512, j.SegmentPx);
        Assert.Equal(5, j.Splits.Count);
        Assert.Equal(new SplitDeRun("00000080:0", 565), j.Splits[2]);
        Assert.Equal(0, SplitsDesFantomes.Lire(Ash)!.Run);
        Assert.Null(SplitsDesFantomes.Lire("pas un fichier de splits"));
        Assert.Null(SplitsDesFantomes.Lire(null));
    }

    [Fact]
    public void L_ecart_est_le_joueur_moins_le_fantome_sur_le_meme_segment()
    {
        var j = SplitsDesFantomes.Lire(Joueur)!;
        var ash = SplitsDesFantomes.Lire(Ash)!;
        Assert.Equal(4, SplitsDesFantomes.EcartEnImages(j.Splits[1], ash));       // 215 - 211 : 4 images de retard
        Assert.Equal(-179, SplitsDesFantomes.EcartEnImages(j.Splits[3], ash));    // le tuyau : 179 images d'avance
        Assert.Null(SplitsDesFantomes.EcartEnImages(j.Splits[2], ash));           // la salle du tuyau : il n'y est pas passe
        Assert.Equal(-2983, SplitsDesFantomes.EnMillisecondes(-179, 60));
        Assert.Equal(67, SplitsDesFantomes.EnMillisecondes(4, 60));
    }

    [Fact]
    public void Ecrire_puis_lire_rend_les_memes_splits()
    {
        var ash = SplitsDesFantomes.Lire(Ash)!;
        var relu = SplitsDesFantomes.Lire(SplitsDesFantomes.Ecrire(ash))!;
        Assert.Equal(ash.Splits, relu.Splits);
        Assert.Equal(512, relu.SegmentPx);
    }

    [Theory]
    [InlineData("0-moi-rp_06GTST28SD87NV5TE9Q4FYECB0.splits", "rp_06GTST28SD87NV5TE9Q4FYECB0")]
    [InlineData("1-rp_ABC.splits", "rp_ABC")]
    [InlineData("joueur.splits", null)]
    [InlineData("1-rp_../x.splits", null)]
    public void Le_replay_se_lit_dans_le_nom_du_fichier(string nom, string? attendu)
        => Assert.Equal(attendu, SplitsDesFantomes.ReplayDuNom(nom));

    [Fact]
    public void Les_splits_ecrits_par_le_coeur_vont_au_cache_et_sortent_du_dossier()
    {
        var avant = FantomesDuSpeedrun.DossierDuCache;
        var dossier = Path.Combine(Path.GetTempPath(), "nelfe-splits-" + Guid.NewGuid().ToString("N"));
        FantomesDuSpeedrun.DossierDuCache = Path.Combine(dossier, "cache");
        try
        {
            Directory.CreateDirectory(dossier);
            File.WriteAllText(Path.Combine(dossier, "1-rp_ASH.splits"), Ash);
            File.WriteAllText(Path.Combine(dossier, "0-moi-rp_MOI.splits"), "NSPLITS1 512\n");   // vide : rien a garder
            File.WriteAllText(Path.Combine(dossier, SplitsDesFantomes.NomDuJoueur), Joueur);
            File.WriteAllText(Path.Combine(dossier, "essai.splits"), Ash);                     // pas un des notres

            FantomesDuSpeedrun.GarderLesSplits(dossier);

            Assert.Equal(Ash, File.ReadAllText(Path.Combine(dossier, "cache", "rp_ASH.splits")));
            Assert.False(File.Exists(Path.Combine(dossier, "cache", "rp_MOI.splits")));
            Assert.False(File.Exists(Path.Combine(dossier, "1-rp_ASH.splits")));
            Assert.False(File.Exists(Path.Combine(dossier, "0-moi-rp_MOI.splits")));
            Assert.False(File.Exists(Path.Combine(dossier, SplitsDesFantomes.NomDuJoueur)));
            Assert.True(File.Exists(Path.Combine(dossier, "essai.splits")));
        }
        finally
        {
            FantomesDuSpeedrun.DossierDuCache = avant;
            if (Directory.Exists(dossier)) Directory.Delete(dossier, true);
        }
    }
}
