using System;
using System.IO;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// ParseGamelistOnly=true etait pose au demarrage par l'installation a la volee, pack ou pas, et
/// jamais retire : EmulationStation ne voyait plus un jeu ajoute a la main (2026-09-26).
/// </summary>
public class ParseGamelistOnlyTests
{
    [Fact]
    public void Sans_pack_il_n_y_a_rien_a_installer_a_la_volee()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "packs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            File.WriteAllText(Path.Combine(dossier, "README.md"), "notice");
            Assert.False(RomPackInstallerService.ContientDesPacks(dossier));

            File.WriteAllText(Path.Combine(dossier, "Lynx (66 jeux).7z"), "x");
            Assert.True(RomPackInstallerService.ContientDesPacks(dossier));
        }
        finally
        {
            Directory.Delete(dossier, recursive: true);
        }
    }

    [Fact]
    public void Un_dossier_absent_ne_contient_pas_de_pack()
        => Assert.False(RomPackInstallerService.ContientDesPacks(Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid().ToString("N"))));

    [Theory]
    [InlineData(null, true)]        // borne d'avant la trace : le reglage ne venait que de nous
    [InlineData("force", true)]     // nous l'avions pose pour des packs, qui ne sont plus la
    [InlineData("relache", false)]  // deja rendu : un true revenu est un choix de l'utilisateur
    public void On_ne_relache_qu_une_fois_ce_que_nous_avons_force(string? etat, bool attendu)
        => Assert.Equal(attendu, RomPackInstallerService.DecisionRelacheParseGamelistOnly(etat));
}
