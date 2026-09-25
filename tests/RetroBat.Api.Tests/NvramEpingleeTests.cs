using System.Text.Json;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La NVRAM que le profil epingle est effacee avant le jeu, pour que la partie parte d'une EEPROM
/// vierge que le jeu initialise a ses reglages d'usine (2026-09-25). Premier motif de refus avant
/// cela : sur 19xx, une EEPROM qui n'etait plus d'usine faisait refuser toutes les parties
/// suivantes, et une copie vierge ecrite par la lecture d'un replay passait avant la vraie.
/// </summary>
public class NvramEpingleeTests
{
    [Fact]
    public void Les_epingles_se_lisent_dans_la_reponse_du_profil()
    {
        using var doc = JsonDocument.Parse(
            """{"profile":{"nvram_pins":[{"file":"fbneo/19xx.nv","cores":["800f"]},{"file":"nvram/19xx/eeprom","cores":[]}]}}""");

        Assert.Equal(new[] { "fbneo/19xx.nv", "nvram/19xx/eeprom" },
            NvramSnapshotService.EpinglesDuProfil(doc.RootElement).ToArray());
    }

    [Fact]
    public void Un_profil_sans_epingle_n_efface_rien()
    {
        using var doc = JsonDocument.Parse("""{"profile":{"bios":{"mode":"none"}}}""");

        Assert.Empty(NvramSnapshotService.EpinglesDuProfil(doc.RootElement));
        Assert.Empty(NvramSnapshotService.EpinglesDuProfil(null));
    }

    [Theory]
    [InlineData("../../Windows/win.ini")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("")]
    public void Une_epingle_ne_designe_jamais_rien_hors_des_sauvegardes(string epingle)
    {
        Assert.Null(NvramSnapshotService.EpingleSure(epingle));
    }

    [Fact]
    public void Une_barre_initiale_ne_fait_pas_sortir_des_sauvegardes()
    {
        const string saves = @"E:\RetroBat\saves";
        var sure = NvramSnapshotService.EpingleSure("/fbneo/19xx.nv");

        Assert.Equal("fbneo/19xx.nv", sure);
        Assert.StartsWith(saves, NvramSnapshotService.Canonique("fbneo", sure!, saves), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Le_coeur_range_sa_NVRAM_sous_le_dossier_du_systeme_et_MAME_autonome_sous_le_sien()
    {
        const string saves = @"E:\RetroBat\saves";

        Assert.Equal(Path.Combine(saves, "fbneo", "fbneo", "19xx.nv"), NvramSnapshotService.Canonique("fbneo", "fbneo/19xx.nv", saves));
        Assert.Equal(Path.Combine(saves, "mame", "nvram", "19xx", "eeprom"), NvramSnapshotService.Canonique("fbneo", "nvram/19xx/eeprom", saves));
    }

    [Fact]
    public void Toutes_les_copies_epinglees_sont_trouvees_et_rien_d_autre()
    {
        var saves = Path.Combine(Path.GetTempPath(), "nelfe-nvram-" + Guid.NewGuid().ToString("N"));
        try
        {
            // L'etat de la borne de test le 2026-09-25 : la vraie EEPROM, la copie ecrite par la
            // lecture d'un replay, le fichier de records (a garder) et un autre jeu (a garder).
            Poser(saves, "fbneo/fbneo/19xx.nv");
            Poser(saves, "fbneo/19xx.nv");
            Poser(saves, "fbneo/fbneo/19xx.hi");
            Poser(saves, "fbneo/fbneo/1942.nv");
            Poser(saves, "mame/nvram/19xx/eeprom");

            var copies = NvramSnapshotService.CopiesEpinglees("fbneo", "19xx", ["fbneo/19xx.nv", "nvram/19xx/eeprom"], saves)
                .Select(c => Path.GetRelativePath(saves, c).Replace('\\', '/'))
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(new[] { "fbneo/19xx.nv", "fbneo/fbneo/19xx.nv", "mame/nvram/19xx/eeprom" }, copies);
        }
        finally
        {
            Directory.Delete(saves, recursive: true);
        }
    }

    [Fact]
    public void Sans_epingle_aucune_copie()
    {
        Assert.Empty(NvramSnapshotService.CopiesEpinglees("fbneo", "19xx", [], @"E:\RetroBat\saves"));
    }

    private static void Poser(string saves, string relatif)
    {
        var chemin = Path.Combine(saves, relatif.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllBytes(chemin, [0xFF]);
    }
}
