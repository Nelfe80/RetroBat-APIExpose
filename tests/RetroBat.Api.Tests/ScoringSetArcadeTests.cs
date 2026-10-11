using System;
using System.IO;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'annonce « Partie certifiable » identifie un jeu d'arcade par le SET lance, plus par son nom :
/// ddragon et doubledr s'appellent tous deux « Double Dragon » (2026-09-25). Une console, elle, ne
/// declare aucun sha1 : son identite est le contenu mesure sur le fichier (2026-10-11).
/// </summary>
public class ScoringSetArcadeTests
{
    [Fact]
    public void En_arcade_le_set_lance_est_transmis()
    {
        Assert.Equal("doubledr", NelfePlayScoringReporter.SetArcade("arcade", " doubledr "));
    }

    [Fact]
    public void Une_console_ne_transmet_aucun_set()
    {
        Assert.Null(NelfePlayScoringReporter.SetArcade("megadrive", "Sonic the Hedgehog (USA, Europe)"));
    }

    [Fact]
    public void Sans_set_connu_rien_n_est_transmis()
    {
        Assert.Null(NelfePlayScoringReporter.SetArcade("arcade", ""));
        Assert.Null(NelfePlayScoringReporter.SetArcade("arcade", null));
    }

    [Fact]
    public void L_arcade_declare_le_sha1_de_son_set_comme_le_referentiel_le_donne()
    {
        Assert.Equal(GamelistIdentity.DeclaredSha1("arcade", "1942", "1942"),
            NelfePlayScoringReporter.Sha1Declare("arcade", "1942", "1942"));
    }

    /// <summary>
    /// Le cas d'Alex Kidd : le referentiel connait le groupe, et sa premiere version (japonaise)
    /// portait le sha1 que toute ROM du groupe declarait. Une console ne declare plus rien.
    /// </summary>
    [Fact]
    public void Une_console_ne_declare_aucun_sha1_meme_quand_le_referentiel_connait_son_groupe()
    {
        var dossier = Path.Combine(AppContext.BaseDirectory, "resources", "gamelist", "systems");
        Directory.CreateDirectory(dossier);
        var catalogue = Path.Combine(dossier, "zz_console_de_test_lt.json");
        File.WriteAllText(catalogue,
            "{\"grp\": \"jeu_de_test_console\", \"set\": \"jeu_de_test_console_jp\", "
            + "\"hsh\": [{\"sha1\": \"62FDC25501E17B87C355378562C3B1966E5F9008\"}]}\n");
        try
        {
            Assert.Equal("62fdc25501e17b87c355378562c3b1966e5f9008",
                GamelistIdentity.DeclaredSha1("zz_console_de_test", "jeu-de-test-console"));
            Assert.Null(NelfePlayScoringReporter.Sha1Declare("zz_console_de_test", "jeu-de-test-console", null));
            Assert.Null(NelfePlayScoringReporter.Sha1Declare("mastersystem", "alex-kidd-in-miracle-world", null));
        }
        finally
        {
            File.Delete(catalogue);
        }
    }
}
