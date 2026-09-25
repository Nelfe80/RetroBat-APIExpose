using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La collection World Scoring n'accueille qu'un contenu CONFIRME (2026-09-25). Un joueur a vu
/// « Partie certifiable » sur un Double Dragon et rien n'est parti : le Double Dragon Neo-Geo
/// (doubledr) porte le meme nom et le meme groupe que celui de Technos (ddragon), ouvert au scoring.
/// Les sha1 ci-dessous sont les vrais, releves dans le referentiel de la borne.
/// </summary>
public class ScoringCollectionContenuTests
{
    private const string Sha1Technos = "1504faaf07c541330cd43b72dc6846911dfd85a3";
    private const string Sha1BiosNeoGeo = "5992277debadeb64d1c1c64b0a92d9293eaf7e4a";

    private static readonly Func<string?, string?, string?, string?> Referentiel = (_, _, set) => set switch
    {
        "ddragon" or "ddragonbl" => Sha1Technos,
        "doubledr" or "mslug3" => Sha1BiosNeoGeo,
        _ => null,
    };

    private static readonly Func<string, string?> AucunCalcul = _ => null;

    private static OpenGame DoubleDragon => new()
    {
        SystemId = "arcade",
        RomGroup = "double-dragon",
        ContentHashes = new ContentHashes { Md5 = ["22beac4a170b45ed13da71baa1426074"], Sha1 = [Sha1Technos] },
    };

    private static InstalledGame Arcade(string chemin, string? md5 = null) => new(
        "fbneo", "arcade", "double-dragon", chemin, Path.GetFileNameWithoutExtension(chemin), md5, null, true, null);

    [Fact]
    public void Le_set_du_jeu_classe_est_confirme_quel_que_soit_son_zip()
    {
        // Le zip du seul joueur classe n'a pas le md5 publie par l'index : c'est le set qui prouve.
        var fichier = Arcade("E:/roms/fbneo/ddragon.zip", md5: "0fe10dcb48a1123069da6b14799f1798");

        Assert.True(NelfePlayScoringCollectionSyncService.ContenuConfirme(fichier, DoubleDragon, Referentiel, AucunCalcul));
    }

    [Fact]
    public void Le_Double_Dragon_Neo_Geo_n_entre_pas_sous_le_meme_nom()
    {
        var fichier = Arcade("E:/roms/fbneo/doubledr.zip");

        Assert.False(NelfePlayScoringCollectionSyncService.ContenuConfirme(fichier, DoubleDragon, Referentiel, AucunCalcul));
    }

    [Fact]
    public void Un_set_inconnu_du_referentiel_n_entre_pas()
    {
        var fichier = Arcade("E:/roms/fbneo/Double Dragon.zip");

        Assert.False(NelfePlayScoringCollectionSyncService.ContenuConfirme(fichier, DoubleDragon, Referentiel, AucunCalcul));
    }

    [Fact]
    public void Un_zip_identique_a_celui_du_profil_suffit()
    {
        var fichier = Arcade("E:/roms/fbneo/renomme.zip", md5: "22beac4a170b45ed13da71baa1426074");

        Assert.True(NelfePlayScoringCollectionSyncService.ContenuConfirme(fichier, DoubleDragon, Referentiel, AucunCalcul));
    }

    [Fact]
    public void Un_profil_sans_aucun_hash_ne_confirme_rien()
    {
        var jeu = new OpenGame { SystemId = "arcade", RomGroup = "double-dragon", ContentHashes = new ContentHashes() };

        Assert.False(NelfePlayScoringCollectionSyncService.ContenuConfirme(
            Arcade("E:/roms/fbneo/ddragon.zip"), jeu, Referentiel, AucunCalcul));
    }

    [Fact]
    public void Une_console_jamais_scrapee_est_confirmee_par_le_md5_calcule()
    {
        var sonic = new OpenGame
        {
            SystemId = "megadrive",
            RomGroup = "sonic-the-hedgehog",
            ContentHashes = new ContentHashes { Md5 = ["1bc674be034e43c96b86487ac69d9293"] },
        };
        var fichier = new InstalledGame("megadrive", "megadrive", "sonic-the-hedgehog", "E:/roms/megadrive/sonic.zip",
            "Sonic", null, null, true, null);

        Assert.True(NelfePlayScoringCollectionSyncService.ContenuConfirme(
            fichier, sonic, Referentiel, _ => "1bc674be034e43c96b86487ac69d9293"));
        Assert.False(NelfePlayScoringCollectionSyncService.ContenuConfirme(
            fichier, sonic, Referentiel, _ => "ffffffffffffffffffffffffffffffff"));
    }

    [Fact]
    public void Le_md5_d_une_cartouche_zippee_est_celui_de_sa_ROM()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "nelfe-md5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            var rom = Encoding.ASCII.GetBytes("SEGA MEGA DRIVE - contenu de test");
            var zip = Path.Combine(dossier, "sonic.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using var flux = archive.CreateEntry("sonic.md").Open();
                flux.Write(rom);
            }
            var brut = Path.Combine(dossier, "sonic.md");
            File.WriteAllBytes(brut, rom);
            var attendu = Convert.ToHexString(MD5.HashData(rom)).ToLowerInvariant();

            Assert.Equal(attendu, NelfePlayScoringCollectionSyncService.Md5DuContenu(zip));
            Assert.Equal(attendu, NelfePlayScoringCollectionSyncService.Md5DuContenu(brut));
        }
        finally
        {
            Directory.Delete(dossier, recursive: true);
        }
    }
}
