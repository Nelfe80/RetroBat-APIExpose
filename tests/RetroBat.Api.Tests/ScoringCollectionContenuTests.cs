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

    // mspacmab (2026-10-18, chez un joueur) : bootleg de Ms. Pac-Man, meme sha1 declare que mspacman (un fichier partage).
    private const string Sha1MsPacMan = "5e8b472b615f12efca3fe792410c23619f067845";

    private static OpenGame MsPacMan(params string[] md5) => new()
    {
        SystemId = "arcade",
        RomGroup = "ms-pac-man",
        ContentHashes = new ContentHashes { Md5 = md5, Sha1 = [Sha1MsPacMan] },
    };

    private static readonly Func<string?, string?, string?, string?> ReferentielPacMan = (_, _, set) =>
        set?.ToLowerInvariant() is "mspacman" or "mspacmab" ? Sha1MsPacMan : null;

    private static readonly Func<string?, string?, bool> Bootlegs = (_, set) => string.Equals(set, "mspacmab", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Le_MEM_nomme_ses_sets_par_le_md5_de_leur_nom()
        => Assert.Equal("01052a074f9e7ce8dc823a5dd2155d14", NelfePlayScoringCollectionSyncService.Md5DuNomDeSet("mspacman"));

    [Fact]
    public void Un_bootleg_au_sha1_du_parent_n_entre_pas_sans_le_MEM()
    {
        var jeu = MsPacMan("01052a074f9e7ce8dc823a5dd2155d14");
        Assert.True(NelfePlayScoringCollectionSyncService.ContenuConfirme(
            Arcade("E:/roms/mame/mspacman.zip"), jeu, ReferentielPacMan, AucunCalcul, Bootlegs));
        Assert.False(NelfePlayScoringCollectionSyncService.ContenuConfirme(
            Arcade("E:/roms/mame/mspacmab.zip"), jeu, ReferentielPacMan, AucunCalcul, Bootlegs));
    }

    [Fact]
    public void Un_bootleg_nomme_par_le_MEM_entre()
    {
        var jeu = MsPacMan("01052a074f9e7ce8dc823a5dd2155d14", NelfePlayScoringCollectionSyncService.Md5DuNomDeSet("mspacmab"));
        Assert.True(NelfePlayScoringCollectionSyncService.ContenuConfirme(
            Arcade("E:/roms/mame/MSPACMAB.zip"), jeu, ReferentielPacMan, AucunCalcul, Bootlegs));
    }

    // La meme regle pour la partie (2026-10-19) : prevol non certifiable, rien n'est soumis.
    [Fact]
    public void Une_partie_sur_un_bootleg_que_le_MEM_ne_nomme_pas_n_est_pas_certifiable()
    {
        var empreintes = new ContentHashes { Md5 = ["01052a074f9e7ce8dc823a5dd2155d14"], Sha1 = [Sha1MsPacMan] };
        Assert.True(NelfePlayScoringReporter.BootlegHorsDefinition("arcade", "ms-pac-man", "mspacmab", empreintes, Bootlegs));
        Assert.False(NelfePlayScoringReporter.BootlegHorsDefinition("arcade", "ms-pac-man", "mspacman", empreintes, Bootlegs));
        var nomme = new ContentHashes { Md5 = [NelfePlayScoringCollectionSyncService.Md5DuNomDeSet("mspacmab")] };
        Assert.False(NelfePlayScoringReporter.BootlegHorsDefinition("arcade", "ms-pac-man", "mspacmab", nomme, Bootlegs));
        // Une console s'identifie par son contenu mesure : la regle ne la touche pas.
        Assert.False(NelfePlayScoringReporter.BootlegHorsDefinition("megadrive", "sonic", "mspacmab", empreintes, Bootlegs));
    }

    [Fact]
    public void Les_empreintes_du_profil_ouvert_se_lisent_dans_l_index_garde_sur_disque()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "nelfe-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            File.WriteAllText(Path.Combine(dossier, "scoring-collection-remote.json"),
                """{"ok":true,"games":[{"system_id":"arcade","rom_group":"ms-pac-man","content_hashes":{"md5":["01052a074f9e7ce8dc823a5dd2155d14"]}}]}""");
            Assert.Equal(["01052a074f9e7ce8dc823a5dd2155d14"], NelfePlayScoringCollectionSyncService.EmpreintesOuvertes("MS-PAC-MAN", dossier)!.Md5);
            Assert.Null(NelfePlayScoringCollectionSyncService.EmpreintesOuvertes("1942", dossier));
            Assert.Null(NelfePlayScoringCollectionSyncService.EmpreintesOuvertes("ms-pac-man", Path.Combine(dossier, "absent")));
        }
        finally
        {
            Directory.Delete(dossier, recursive: true);
        }
    }

    // 24/10 : « Sonic The Hedgehog (Japan) (En) (Virtual Console) », dans la collection par son contenu, cherchait le profil
    // du nom de son fichier. Une console s'identifie par son contenu mesure, quel que soit le nom.
    [Fact]
    public void Une_partie_console_trouve_son_jeu_par_le_contenu_et_jamais_par_le_nom_du_fichier()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "nelfe-index-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            File.WriteAllText(Path.Combine(dossier, "scoring-collection-remote.json"),
                """{"ok":true,"games":[{"system_id":"megadrive","rom_group":"sonic-the-hedgehog","content_hashes":{"md5":["09dadb5071eb35050067a32462e39c5f"],"sha256":["46160baa06362c711c9f1a5017cb7371026444936c8af5e93a78996cf32ff2a6"]}},{"system_id":"arcade","rom_group":"ms-pac-man","content_hashes":{"md5":["01052a074f9e7ce8dc823a5dd2155d14"]}}]}""");
            Assert.Equal("sonic-the-hedgehog", NelfePlayScoringReporter.GroupeDeLaPartie("megadrive", "sonic-the-hedgehog-japan-en-virtual-console",
                "09DADB5071EB35050067A32462E39C5F", null, dossier));
            Assert.Equal("sonic-the-hedgehog", NelfePlayScoringReporter.GroupeDeLaPartie("megadrive", "n-importe-quoi",
                null, "46160baa06362c711c9f1a5017cb7371026444936c8af5e93a78996cf32ff2a6", dossier));
            // Un contenu inconnu garde le groupe du wrapper : la plateforme dira « non ouvert ».
            Assert.Equal("sonic-the-hedgehog-japan-en-virtual-console", NelfePlayScoringReporter.GroupeDeLaPartie("megadrive",
                "sonic-the-hedgehog-japan-en-virtual-console", "00000000000000000000000000000000", null, dossier));
            // Le meme md5 sur un autre systeme ne vaut rien ; l'arcade garde son set (le md5 de son nom n'est pas un contenu).
            Assert.Null(NelfePlayScoringCollectionSyncService.GroupeOuvertDuContenu("snes", "09dadb5071eb35050067a32462e39c5f", null, dossier));
            Assert.Null(NelfePlayScoringCollectionSyncService.GroupeOuvertDuContenu("arcade", "01052a074f9e7ce8dc823a5dd2155d14", null, dossier));
        }
        finally
        {
            Directory.Delete(dossier, recursive: true);
        }
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
