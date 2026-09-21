using System.IO.Compression;
using RetroBat.Api.Replay.Playback;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La reconnaissance de la ROM d'un replay. RetroArch n'annonce pas la meme empreinte
/// selon le core : pour une console il hache la ROM extraite du zip (le CRC que l'archive
/// stocke pour l'entree), pour l'arcade il hache le zip entier. Les deux doivent etre
/// reconnues, sinon un replay d'arcade ne se rejoue que sur la borne qui l'a enregistre.
/// </summary>
public sealed class ReplayRomMatchTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "nelfe-replay-rom-" + Guid.NewGuid().ToString("N"));

    public ReplayRomMatchTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* le dossier temporaire ne compte pas */ }
    }

    [Fact]
    public void Un_zip_de_console_est_reconnu_par_le_crc_de_son_entree()
    {
        var contenu = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var zip = Path.Combine(_dossier, "Sonic The Hedgehog (USA, Europe).zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var s = archive.CreateEntry("Sonic The Hedgehog (USA, Europe).md").Open();
            s.Write(contenu);
        }
        var crcEntree = Crc32(contenu);

        Assert.True(ReplayRuntimeResolver.PorteEmpreinte(zip, crcEntree, string.Empty, limiteOctets: 0));
        Assert.False(ReplayRuntimeResolver.PorteEmpreinte(zip, "00000000", string.Empty, limiteOctets: 0));
    }

    [Fact]
    public void Un_zip_d_arcade_est_reconnu_par_le_crc_du_fichier_entier()
    {
        var zip = Path.Combine(_dossier, "1942.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var nom in new[] { "srb-03.m3", "srb-04.m4", "srb-05.m5" })
            {
                using var s = archive.CreateEntry(nom).Open();
                s.Write(new byte[] { (byte) nom.Length, 0x42, 0x19 });
            }
        }
        var crcZip = Crc32(File.ReadAllBytes(zip));

        Assert.True(ReplayRuntimeResolver.PorteEmpreinte(zip, crcZip, string.Empty, limiteOctets: 0));
        // Au-dessus de la limite, le fichier entier n'est pas lu : seules les entrees comptent.
        Assert.False(ReplayRuntimeResolver.PorteEmpreinte(zip, crcZip, string.Empty, limiteOctets: 1));
    }

    [Fact]
    public void Un_fichier_nu_est_reconnu_par_son_sha256_aussi()
    {
        var rom = Path.Combine(_dossier, "jeu.md");
        var contenu = new byte[] { 0xAA, 0xBB, 0xCC };
        File.WriteAllBytes(rom, contenu);
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(contenu)).ToLowerInvariant();

        Assert.True(ReplayRuntimeResolver.PorteEmpreinte(rom, "ffffffff", sha, limiteOctets: 0));
        Assert.True(ReplayRuntimeResolver.PorteEmpreinte(rom, Crc32(contenu), string.Empty, limiteOctets: 0));
        Assert.False(ReplayRuntimeResolver.PorteEmpreinte(rom, "ffffffff", "deadbeef", limiteOctets: 0));
    }

    [Theory]
    [InlineData(@"C:\roms\megadrive\Sonic The Hedgehog (USA, Europe).zip", "sonic-the-hedgehog-usa-europe", true, true)]
    [InlineData(@"C:\roms\megadrive\Sonic The Hedgehog (Japan, Korea).zip", "sonic-the-hedgehog-usa-europe", false, true)]
    [InlineData(@"C:\roms\megadrive\Sonic The Hedgehog 2 (World).zip", "sonic-the-hedgehog-usa-europe", false, false)]
    [InlineData(@"C:\roms\arcade\1942.zip", "1942", true, true)]
    [InlineData(@"C:\roms\arcade\1943.zip", "1942", false, false)]
    [InlineData(@"C:\roms\arcade\a.zip", "a", true, false)]
    public void Le_meme_jeu_se_reconnait_a_son_nom_puis_a_son_titre(string fichier, string attendu, bool memeNom, bool memeTitre)
    {
        Assert.Equal(memeNom, ReplayRuntimeResolver.MemeNom(fichier, attendu));
        Assert.Equal(memeTitre, ReplayRuntimeResolver.MemeTitre(fichier, attendu));
    }

    private static string Crc32(byte[] data)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = (crc >> 8) ^ table[(crc ^ b) & 0xFF];
        return (crc ^ 0xFFFFFFFFu).ToString("x8");
    }
}
