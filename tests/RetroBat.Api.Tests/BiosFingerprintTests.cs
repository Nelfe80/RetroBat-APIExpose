using System.Text.Json;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les BIOS d'une partie certifiee : la borne hache ce que le PROFIL nomme, la ou le coeur le
/// charge. Un profil sans BIOS ne coute rien ; un nom ne sort jamais des dossiers de recherche.
/// </summary>
public class BiosFingerprintTests
{
    private static List<string> Exiges(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return BiosFingerprintService.FichiersExiges(doc.RootElement);
    }

    [Fact]
    public void Un_profil_sans_bios_n_exige_rien()
    {
        Assert.Empty(Exiges("""{ "bios": { "mode": "none" } }"""));
        Assert.Empty(Exiges("""{ "metric": {} }"""));
        Assert.Empty(Exiges("""{ "bios": null }"""));
    }

    [Fact]
    public void Un_profil_a_bios_nomme_ses_fichiers_une_fois()
    {
        var noms = Exiges("""
            { "bios": { "mode": "files", "files": [
              { "name": "neogeo.zip", "allowed_sha256": ["aa"] },
              { "name": "NEOGEO.zip", "allowed_sha256": ["bb"], "cores": ["cc"] } ] } }
            """);
        Assert.Equal(new[] { "neogeo.zip" }, noms);
    }

    [Fact]
    public void Le_bios_se_cherche_d_abord_a_cote_de_la_rom()
    {
        var racine = Path.Combine(Path.GetTempPath(), "bios-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var roms = Directory.CreateDirectory(Path.Combine(racine, "roms", "neogeo")).FullName;
            var bios = Directory.CreateDirectory(Path.Combine(racine, "bios")).FullName;
            Directory.CreateDirectory(Path.Combine(bios, "fbneo"));
            File.WriteAllText(Path.Combine(bios, "neogeo.zip"), "systeme");
            var jeu = Path.Combine(roms, "mslug.zip");

            Assert.Equal(Path.Combine(bios, "neogeo.zip"), BiosFingerprintService.Trouver("neogeo.zip", jeu, bios));

            File.WriteAllText(Path.Combine(bios, "fbneo", "neogeo.zip"), "fbneo");
            Assert.Equal(Path.Combine(bios, "fbneo", "neogeo.zip"), BiosFingerprintService.Trouver("neogeo.zip", jeu, bios));

            File.WriteAllText(Path.Combine(roms, "neogeo.zip"), "rom");
            Assert.Equal(Path.Combine(roms, "neogeo.zip"), BiosFingerprintService.Trouver("neogeo.zip", jeu, bios));
        }
        finally
        {
            try { Directory.Delete(racine, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Un_nom_ne_remonte_jamais_hors_des_dossiers()
    {
        Assert.Null(BiosFingerprintService.Trouver("../appsettings.json", @"C:\roms\neogeo\mslug.zip", @"C:\bios"));
        Assert.Null(BiosFingerprintService.Trouver("", null, @"C:\bios"));
    }
}
