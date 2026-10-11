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

    /// <summary>
    /// La recherche du fichier exact sous un autre nom a un budget (2026-10-10) : 1 963 fichiers relus, 3 min 40 sur
    /// l'ecran de lecture, pour un dump d'arcade que la borne n'avait pas.
    /// </summary>
    [Fact]
    public void La_recherche_du_fichier_exact_trouve_dans_le_budget_et_s_arrete_au_dela()
    {
        var candidats = Enumerable.Range(0, 5).Select(i =>
        {
            var f = Path.Combine(_dossier, "jeu-" + i + ".bin");
            File.WriteAllBytes(f, new byte[] { (byte) i, 7, 7 });
            return f;
        }).ToList();
        var crcDuQuatrieme = Crc32(new byte[] { 3, 7, 7 });
        var vus = new List<int>();

        var trouve = ReplayRuntimeResolver.FichierExact(candidats, crcDuQuatrieme, string.Empty, TimeSpan.FromMinutes(1),
            (lus, total) => vus.Add(lus), out var lusAvant);
        Assert.Equal(candidats[3], trouve);
        Assert.Equal(3, lusAvant);
        Assert.Equal(new[] { 0, 1, 2, 3 }, vus);

        Assert.Null(ReplayRuntimeResolver.FichierExact(candidats, "00000000", string.Empty, TimeSpan.FromMinutes(1), null, out var tousLus));
        Assert.Equal(5, tousLus);

        // Budget epuise : on s'arrete avant de lire, et le nombre lu dit que la recherche n'est pas allee au bout.
        Assert.Null(ReplayRuntimeResolver.FichierExact(candidats, crcDuQuatrieme, string.Empty, TimeSpan.Zero, null, out var aucun));
        Assert.Equal(0, aucun);
    }

    /// <summary>
    /// La borne reconnait d'abord le jeu, sans rien hacher (2026-10-11). Un replay de Double Dragon enregistre sur un
    /// autre dump : elle lisait 190 des 1 960 fichiers de roms\fbneo pendant vingt secondes, puis lancait le ddragon.zip
    /// qu'elle avait sous les yeux. Le referentiel des jeux (resources/gamelist/systems) dit quels fichiers sont le meme
    /// jeu : toutes les versions d'un jeu y portent le meme groupe.
    /// </summary>
    [Fact]
    public void Le_referentiel_dit_quels_fichiers_sont_le_meme_jeu_sans_rien_lire()
    {
        // Trois fiches comme celles de fbneo_lt.json : deux versions de Double Dragon, et sa suite.
        File.WriteAllLines(Path.Combine(_dossier, "fbneo_lt.json"), new[]
        {
            """{"sys": "fbneo", "t": "game", "id": "ddragon", "set": "ddragon", "grp": "double_dragon", "fn": "Double Dragon (World set 1)", "n": "Double Dragon"}""",
            """{"sys": "fbneo", "t": "game", "id": "ddragonu", "set": "ddragonu", "grp": "double_dragon", "fn": "Double Dragon (US set 1)", "n": "Double Dragon"}""",
            """{"sys": "fbneo", "t": "game", "id": "ddragon2", "set": "ddragon2", "grp": "double_dragon_ii_-_the_revenge", "fn": "Double Dragon II - The Revenge (World)", "n": "Double Dragon II"}""",
        });
        var referentiel = new RetroBat.Api.Media.RomCanonicalResolver(racineDuReferentiel: _dossier);
        string? JeuDe(string fichier) => referentiel.ResolveByRomName("fbneo", Path.GetFileName(fichier))?.GameKey;
        // Le jeu du replay se retrouve par le nom que porte son manifeste (« fb-alpha/ddragon »).
        var jeu = referentiel.ResolveByRomName("fbneo", "ddragon")?.GameKey;
        Assert.Equal("fbneo/double-dragon", jeu);

        var fichiers = new[]
        {
            @"E:\roms\fbneo\1942.zip", @"E:\roms\fbneo\ddragon2.zip", @"E:\roms\fbneo\ddragonu.zip",
            @"E:\roms\fbneo\ddragon.zip", @"E:\roms\mame\ddragon.zip", @"E:\roms\fbneo\inconnu.zip",
        };
        // Les versions du meme jeu, tous dossiers confondus ; celles qui portent le nom du replay d'abord. Ni la
        // suite du jeu, ni un fichier que le referentiel ne connait pas.
        Assert.Equal(
            new[] { @"E:\roms\fbneo\ddragon.zip", @"E:\roms\mame\ddragon.zip", @"E:\roms\fbneo\ddragonu.zip" },
            ReplayRuntimeResolver.MemeJeu(jeu!, fichiers, JeuDe, "ddragon"));
        Assert.Empty(ReplayRuntimeResolver.MemeJeu(jeu!, new[] { @"E:\roms\fbneo\ddragon2.zip" }, JeuDe, "ddragon"));
    }

    [Fact]
    public void Les_fichiers_proches_se_comparent_dans_l_ordre_du_plus_sur_au_moins_sur_et_une_seule_fois()
    {
        var parNom = new[] { @"E:\roms\megadrive\Sonic The Hedgehog (USA, Europe).zip" };
        var memeJeu = new[] { @"E:\roms\megadrive\Sonic The Hedgehog (USA, Europe).zip", @"E:\roms\megadrive\Sonic The Hedgehog (Japan).zip" };
        var memeTitre = new[] { @"e:\roms\megadrive\sonic the hedgehog (japan).zip", @"E:\roms\megadrive\Sonic The Hedgehog (Beta).zip" };

        Assert.Equal(
            new[]
            {
                @"E:\roms\megadrive\Sonic The Hedgehog (USA, Europe).zip",
                @"E:\roms\megadrive\Sonic The Hedgehog (Japan).zip",
                @"E:\roms\megadrive\Sonic The Hedgehog (Beta).zip",
            },
            ReplayRuntimeResolver.Proches(parNom, memeJeu, memeTitre));
        Assert.Empty(ReplayRuntimeResolver.Proches(Array.Empty<string>(), Array.Empty<string>()));
    }

    /// <summary>
    /// En arcade, le coeur reconnait le jeu au nom de son archive : chercher le meme contenu sous un autre nom ne sert a
    /// rien, et la borne ne lit jamais le dossier pour cela.
    /// </summary>
    [Theory]
    [InlineData("fbneo", "fbneo", true)]
    [InlineData("mame", "mame", true)]
    [InlineData("arcade", null, true)]
    [InlineData("neogeo", "fbneo", true)]
    [InlineData("naomi", "mame2010", true)]
    [InlineData("megadrive", "genesis_plus_gx", false)]
    [InlineData("snes", "snes9x", false)]
    [InlineData("megadrive", null, false)]
    [InlineData(null, null, false)]
    public void Seul_un_systeme_qui_n_attache_pas_le_jeu_au_nom_du_fichier_se_cherche_par_contenu(string? systeme, string? coeur, bool nomLie)
        => Assert.Equal(nomLie, ReplayRuntimeResolver.NomLieAuJeu(systeme, coeur));

    [Fact]
    public void La_recherche_par_contenu_lit_d_abord_les_en_tetes_des_archives()
    {
        var contenu = new byte[] { 9, 8, 7, 6, 5 };
        var zip = Path.Combine(_dossier, "Autre nom.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var s = archive.CreateEntry("jeu.md").Open();
            s.Write(contenu);
        }
        var nu = Path.Combine(_dossier, "nu.md");
        File.WriteAllBytes(nu, contenu);
        var crc = Crc32(contenu);

        // L'entree d'une archive se reconnait a son CRC sans lire le fichier ; un fichier nu n'a pas d'entree.
        Assert.True(ReplayRuntimeResolver.PorteEntree(zip, crc));
        Assert.False(ReplayRuntimeResolver.PorteEntree(nu, crc));
        Assert.False(ReplayRuntimeResolver.PorteEntree(zip, string.Empty));
        // Le fichier entier, lui : le fichier nu porte ce CRC, l'archive en porte un autre (celui du zip).
        Assert.True(ReplayRuntimeResolver.PorteFichier(nu, crc, string.Empty, limiteOctets: 0));
        Assert.False(ReplayRuntimeResolver.PorteFichier(zip, crc, string.Empty, limiteOctets: 0));
        Assert.False(ReplayRuntimeResolver.PorteFichier(nu, crc, string.Empty, limiteOctets: 1));

        var candidats = new[] { nu, zip };
        Assert.Equal(zip, ReplayRuntimeResolver.FichierExact(candidats, f => ReplayRuntimeResolver.PorteEntree(f, crc), TimeSpan.FromMinutes(1), null, out var lus));
        Assert.Equal(1, lus);
    }

    // ── Le contenu de l'archive (2026-10-11) ──────────────────────────────────────────────────

    /// <summary>Un zip d'arcade : ces fichiers, dans cet ordre, avec cette compression.</summary>
    private string Zip(string nom, IEnumerable<(string Nom, byte[] Octets)> fichiers, CompressionLevel compression = CompressionLevel.Optimal)
    {
        var chemin = Path.Combine(_dossier, nom);
        using (var archive = ZipFile.Open(chemin, ZipArchiveMode.Create))
        {
            foreach (var (fichier, octets) in fichiers)
            {
                using var s = archive.CreateEntry(fichier, compression).Open();
                s.Write(octets);
            }
        }
        return chemin;
    }

    private static readonly (string Nom, byte[] Octets)[] Parent =
    {
        ("21j-1-5.26", new byte[] { 1, 2, 3, 4 }), ("21j-2-3.25", new byte[] { 5, 6, 7, 8 }), ("21j-a.ic26", new byte[] { 9, 10, 11 }),
    };

    [Fact]
    public void Le_contenu_d_un_zip_se_lit_dans_son_en_tete_trie_par_nom_sans_dossier_ni_fichier_vide()
    {
        var zip = Zip("ddragon.zip", Parent.Reverse().Append(("vide.bin", Array.Empty<byte>())).Append(("sous/", Array.Empty<byte>())));

        var contenu = ContenuDArchive.Lire(zip);

        Assert.NotNull(contenu);
        Assert.Equal(new[] { "21j-1-5.26", "21j-2-3.25", "21j-a.ic26" }, contenu!.Select(f => f.Name));
        Assert.Equal(Crc32(new byte[] { 1, 2, 3, 4 }), contenu[0].Crc32);
        Assert.Null(ContenuDArchive.Lire(Path.Combine(_dossier, "absent.zip")));
        var nu = Path.Combine(_dossier, "jeu.md");
        File.WriteAllBytes(nu, new byte[] { 1 });
        Assert.Null(ContenuDArchive.Lire(nu));
    }

    /// <summary>
    /// Le cas du Double Dragon du 2026-10-11 : le meme jeu, zippe autrement, n'a pas la meme empreinte de fichier. Son
    /// contenu, lui, est le meme : c'est le fichier exact, sans avertissement.
    /// </summary>
    [Fact]
    public void Le_meme_jeu_zippe_autrement_est_le_fichier_exact()
    {
        var chezLui = Zip("ddragon-a.zip", Parent, CompressionLevel.Optimal);
        var ici = Zip("ddragon-b.zip", Parent.Reverse(), CompressionLevel.NoCompression);
        var enregistrement = ContenuDArchive.Lire(chezLui);
        var crcDuZip = Crc32(File.ReadAllBytes(chezLui));

        Assert.False(ReplayRuntimeResolver.PorteEmpreinte(ici, crcDuZip, string.Empty, limiteOctets: 0));
        Assert.True(ContenuDArchive.MemeJeu(ici, enregistrement));
        Assert.Equal(new ComparaisonDArchives(true, 0, 3), ContenuDArchive.Comparer(enregistrement, ContenuDArchive.Lire(ici)));
    }

    [Fact]
    public void Un_set_fusionne_avec_ses_clones_et_un_set_separe_sont_le_meme_jeu_dans_les_deux_sens()
    {
        var fusionne = ContenuDArchive.Lire(Zip("fusionne.zip", Parent.Append(("ddragonu.26", new byte[] { 42, 43 }))));
        var separe = ContenuDArchive.Lire(Zip("separe.zip", Parent));

        Assert.True(ContenuDArchive.Comparer(fusionne, separe)!.Meme);
        Assert.True(ContenuDArchive.Comparer(separe, fusionne)!.Meme);
    }

    [Fact]
    public void Une_autre_version_du_jeu_dit_combien_de_fichiers_different()
    {
        var enregistrement = ContenuDArchive.Lire(Zip("rev-a.zip", Parent));
        var revisionB = ContenuDArchive.Lire(Zip("rev-b.zip", Parent.Take(2).Append(("21j-a.ic26", new byte[] { 99, 98, 97 }))));
        var autreJeu = ContenuDArchive.Lire(Zip("autre.zip", new[] { ("x.bin", new byte[] { 7, 7, 7 }) }));

        Assert.Equal(new ComparaisonDArchives(false, 1, 3), ContenuDArchive.Comparer(enregistrement, revisionB));
        Assert.Equal(new ComparaisonDArchives(false, 3, 3), ContenuDArchive.Comparer(enregistrement, autreJeu));
        // Un manifeste d'avant ce changement ne dit pas ses fichiers : rien a comparer, l'empreinte du zip reste seule juge.
        Assert.Null(ContenuDArchive.Comparer(null, revisionB));
        Assert.False(ContenuDArchive.MemeJeu(Path.Combine(_dossier, "rev-b.zip"), null));
    }

    [Fact]
    public void Le_manifeste_porte_les_fichiers_de_l_archive_et_un_ancien_manifeste_se_relit_sans_eux()
    {
        var racine = Path.Combine(_dossier, "magasin");
        var magasin = new RetroBat.Api.Replay.Storage.ReplayStore(Microsoft.Extensions.Logging.Abstractions.NullLogger<RetroBat.Api.Replay.Storage.ReplayStore>.Instance, racine);
        var fichiers = ContenuDArchive.Lire(Zip("ddragon.zip", Parent));
        RetroBat.Api.Replay.Models.ReplayManifest Manifeste(string id, IReadOnlyList<RetroBat.Api.Replay.Models.ReplayRomFile>? rom) => new(
            RetroBat.Api.Replay.Models.ReplayManifest.SchemaId, id, "sess_x",
            new RetroBat.Api.Replay.Models.ReplayGame("fb-alpha/ddragon", "fb_alpha", "double-dragon", null, "caefab07", "fbneo"),
            DateTime.UtcNow, "home",
            new RetroBat.Api.Replay.Models.ReplayRuntime("nelfe-fb-alpha-r1", "1.22.2", "a", "b", null, null, "bsv", "fbneo", rom),
            new RetroBat.Api.Replay.Models.ReplayObjectRef(new string('c', 64), 10),
            new RetroBat.Api.Replay.Models.ReplayFrames(0, null, null, 100, 59.1),
            null, new RetroBat.Api.Replay.Models.ReplayRecovery(false));

        magasin.SaveManifest(Manifeste("rp_avec", fichiers));
        magasin.SaveManifest(Manifeste("rp_sans", null));

        Assert.Contains("\"rom_files\"", File.ReadAllText(magasin.ManifestPath("rp_avec")));
        Assert.DoesNotContain("rom_files", File.ReadAllText(magasin.ManifestPath("rp_sans")));
        Assert.Equal(fichiers, magasin.GetManifest("rp_avec")!.Runtime.RomFiles);
        Assert.Null(magasin.GetManifest("rp_sans")!.Runtime.RomFiles);
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
