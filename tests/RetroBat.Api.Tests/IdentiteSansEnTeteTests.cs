using System.IO.Compression;
using System.Security.Cryptography;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using RetroBat.Api.MassRun;
using SharpCompress.Common;
using SharpCompress.Writers;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE SUPPORT DU 08/11 (neogeoff) : Super Mario Bros. 3 et Mega Man 2 No-Intro refuses « pas la version classee » parce que
/// leur en-tete NES 2.0 n'est pas l'en-tete iNES du dump d'ouverture. Le meme jeu se reconnait desormais sans son en-tete,
/// comme RetroAchievements, et un .7z se lit comme un .zip.
/// </summary>
public sealed class IdentiteSansEnTeteTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "nelfe-rhash-" + Guid.NewGuid().ToString("N"));

    public IdentiteSansEnTeteTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
    }

    private static string Md5(ReadOnlySpan<byte> octets) => Convert.ToHexString(MD5.HashData(octets)).ToLowerInvariant();

    private static readonly byte[] Corps = CorpsDeRom();

    private static byte[] CorpsDeRom()
    {
        var corps = new byte[2 * 16384 + 8192];
        new Random(3).NextBytes(corps);
        return corps;
    }

    /// <summary>Le dump d'ouverture (GoodNES, iNES 1.0) et le No-Intro (NES 2.0) : meme corps, autre en-tete.</summary>
    private static byte[] Ines => [0x4E, 0x45, 0x53, 0x1A, 0x02, 0x01, 0x40, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. Corps];

    private static byte[] Nes2 => [0x4E, 0x45, 0x53, 0x1A, 0x02, 0x01, 0x40, 0x08, 0, 0, 0x07, 0, 0, 0, 0, 0x01, .. Corps];

    private static readonly Func<string?, string?, string?, string?> AucunSet = (_, _, _) => null;

    private static OpenGame Smb3(IReadOnlyList<string>? rhash, params string[] md5) => new()
    {
        SystemId = "nes",
        RomGroup = "super-mario-bros-3",
        ContentHashes = new ContentHashes { Md5 = md5, Rhash = rhash },
    };

    private static InstalledGame Nes(string chemin, string? md5 = null, string? cheevos = null)
        => new("nes", "nes", "super-mario-bros-3", chemin, Path.GetFileNameWithoutExtension(chemin), md5, cheevos, true, null);

    private string Ecrire(string nom, byte[] octets)
    {
        var chemin = Path.Combine(_dossier, nom);
        File.WriteAllBytes(chemin, octets);
        return chemin;
    }

    private bool Confirme(InstalledGame candidat, OpenGame jeu)
        => NelfePlayScoringCollectionSyncService.ContenuConfirme(candidat, jeu, AucunSet, NelfePlayScoringCollectionSyncService.Md5DuContenu,
            null, c => NelfePlayScoringCollectionSyncService.SansEnTeteDuContenu(c.AbsolutePath, c.FrontendSystemId));

    [Fact]
    public void Le_no_intro_entre_par_l_empreinte_sans_en_tete_que_le_site_publie()
    {
        var noIntro = Ecrire("Super Mario Bros. 3 (USA) (Rev 1).nes", Nes2);
        // Avant : le seul md5 du dump d'ouverture, refuse.
        Assert.False(Confirme(Nes(noIntro), Smb3(null, Md5(Ines))));
        // Apres : le site publie l'empreinte sans en-tete du jeu.
        Assert.True(Confirme(Nes(noIntro), Smb3([Md5(Corps)], Md5(Ines))));
        // Le dump d'ouverture reste reconnu par son md5, comme avant.
        Assert.True(Confirme(Nes(Ecrire("Super Mario Bros 3 (U) (PRG 1).nes", Ines)), Smb3([Md5(Corps)], Md5(Ines))));
    }

    [Fact]
    public void Un_md5_sans_en_tete_du_referentiel_vaut_aussi_empreinte_sans_en_tete()
    {
        // Le referentiel range parfois le md5 du corps nu parmi les md5 (SMB1 : 8e363018...) : il suffit, sans liste rhash.
        Assert.True(Confirme(Nes(Ecrire("smb3.nes", Nes2)), Smb3(null, Md5(Ines), Md5(Corps))));
    }

    [Fact]
    public void Le_hash_retroachievements_de_la_gamelist_vaut_l_empreinte_sans_en_tete()
    {
        var jeu = Smb3([Md5(Corps)], Md5(Ines));
        Assert.True(NelfePlayScoringCollectionSyncService.ContenuConfirme(Nes("E:/absent/smb3.nes", cheevos: Md5(Corps)), jeu, AucunSet, _ => null));
    }

    [Fact]
    public void Un_autre_jeu_n_entre_pas_et_l_arcade_ne_regarde_jamais_le_contenu()
    {
        var autre = (byte[])Nes2.Clone();
        autre[100] ^= 0xFF;
        Assert.False(Confirme(Nes(Ecrire("autre.nes", autre)), Smb3([Md5(Corps)], Md5(Ines))));

        // Arcade : meme si un md5 « sans en-tete » tombait juste, le set reste l'identite.
        var arcade = new OpenGame { SystemId = "arcade", RomGroup = "x", ContentHashes = new ContentHashes { Rhash = [Md5(Corps)] } };
        var zip = Ecrire("x.zip", []);
        var candidat = new InstalledGame("fbneo", "arcade", "x", zip, "x", null, Md5(Corps), true, null);
        Assert.False(NelfePlayScoringCollectionSyncService.ContenuConfirme(candidat, arcade, AucunSet, _ => Md5(Corps),
            null, _ => Md5(Corps)));
    }

    [Fact]
    public void Une_console_sans_regle_garde_le_md5_du_fichier()
    {
        var rom = Ecrire("sonic.md", Corps);
        Assert.Null(NelfePlayScoringCollectionSyncService.SansEnTeteDuContenu(rom, "megadrive"));
        Assert.Null(NelfePlayScoringCollectionSyncService.SansEnTeteDuContenu(rom, "fbneo"));
        Assert.Equal(Md5(Corps), NelfePlayScoringCollectionSyncService.SansEnTeteDuContenu(rom, "snes"));
    }

    private string SeptZip(string nom, params (string Entree, byte[] Octets)[] fichiers)
    {
        var chemin = Path.Combine(_dossier, nom);
        using (var flux = File.Create(chemin))
        using (var ecrivain = WriterFactory.OpenWriter(flux, ArchiveType.SevenZip, new WriterOptions(CompressionType.LZMA)))
        {
            foreach (var (entree, octets) in fichiers) ecrivain.Write(entree, new MemoryStream(octets), DateTime.UtcNow);
        }
        return chemin;
    }

    [Fact]
    public void Un_7z_a_un_seul_fichier_se_lit_comme_un_zip()
    {
        var septZip = SeptZip("Super Mario Bros. 3 (USA) (Rev 1).7z", ("Super Mario Bros. 3 (USA) (Rev 1).nes", Nes2));
        Assert.Equal(Md5(Nes2), NelfePlayScoringCollectionSyncService.Md5DuContenu(septZip));
        Assert.Equal(Md5(Corps), NelfePlayScoringCollectionSyncService.SansEnTeteDuContenu(septZip, "nes"));
        Assert.True(Confirme(Nes(septZip), Smb3([Md5(Corps)], Md5(Ines))));
        // Le crc du contenu (MASS RUN, coeur hote des fantomes) : celui du fichier, pas de l'archive.
        var nu = Ecrire("nu.nes", Nes2);
        Assert.Equal(JeuxMassRun.ContenuDe(nu), JeuxMassRun.ContenuDe(septZip));
        // La ROM n'est pas gardee ouverte : le joueur peut la deplacer ou la supprimer (SharpCompress ne la refermait pas).
        File.Delete(septZip);
        Assert.False(File.Exists(septZip));
    }

    [Fact]
    public void Un_7z_a_plusieurs_fichiers_n_a_pas_de_contenu_unique()
    {
        var septZip = SeptZip("deux.7z", ("a.nes", Ines), ("b.nes", Nes2));
        Assert.Null(NelfePlayScoringCollectionSyncService.Md5DuContenu(septZip));
        Assert.Null(JeuxMassRun.ContenuDe(septZip));
    }

    [Fact]
    public void Un_7z_abime_ne_fait_jamais_tomber_la_collection()
    {
        var abime = Ecrire("abime.7z", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4, 1, 2, 3]);
        Assert.Null(NelfePlayScoringCollectionSyncService.Md5DuContenu(abime));
        Assert.Null(NelfePlayScoringCollectionSyncService.SansEnTeteDuContenu(abime, "nes"));
    }

    [Fact]
    public void Le_zip_garde_son_comportement_et_gagne_l_empreinte_sans_en_tete()
    {
        var zip = Path.Combine(_dossier, "smb3.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var flux = archive.CreateEntry("smb3.nes").Open();
            flux.Write(Nes2);
        }
        Assert.Equal(Md5(Nes2), NelfePlayScoringCollectionSyncService.Md5DuContenu(zip));
        Assert.Equal(Md5(Corps), NelfePlayScoringCollectionSyncService.SansEnTeteDuContenu(zip, "nes"));
    }

    [Fact]
    public void Le_plafond_releve_de_multiplayer_n_herite_pas_d_un_refus_sous_le_plafond_ordinaire()
    {
        var rom = Ecrire("gros.nes", Ines);
        Assert.Null(NelfePlayScoringCollectionSyncService.Md5DuContenu(rom, 100));
        Assert.Equal(Md5(Ines), NelfePlayScoringCollectionSyncService.Md5DuContenu(rom, 1_000_000));
    }

    [Fact]
    public void La_partie_trouve_son_jeu_par_l_empreinte_sans_en_tete()
    {
        File.WriteAllText(Path.Combine(_dossier, "scoring-collection-remote.json"),
            $$$"""{"ok":true,"games":[{"system_id":"nes","rom_group":"super-mario-bros","content_hashes":{"md5":["{{{Md5(Ines)}}}"],"rhash":["{{{Md5(Corps)}}}"]}}]}""");
        Assert.Equal("super-mario-bros", NelfePlayScoringReporter.GroupeDeLaPartie("nes", "super-mario-bros-world", Md5(Nes2), null, _dossier,
            sansEnTete: Md5(Corps)));
        Assert.Equal("super-mario-bros-world", NelfePlayScoringReporter.GroupeDeLaPartie("nes", "super-mario-bros-world", Md5(Nes2), null, _dossier));
    }

    [Fact]
    public void Les_coeurs_hotes_se_publient_avec_le_crc_et_le_nom_de_leur_dump()
    {
        // Neuf depuis Mega Man X (1.9.170)
        var hotes = RetroBat.Api.Speedrun.FantomesDuSpeedrun.Liste;
        Assert.Equal(9, hotes.Count);
        Assert.All(hotes, h =>
        {
            Assert.Matches("^[0-9a-f]{8}$", h.Crc);
            Assert.False(string.IsNullOrWhiteSpace(h.Dump));
        });
        Assert.Contains(hotes, h => h.Groupe == "super-mario-bros-3" && h.Crc == "0b742b33");
        Assert.Contains(hotes, h => h.Groupe == "mega-man-2" && h.Crc == "a9bd44bc");
        Assert.Contains(hotes, h => h.Groupe == "mega-man-x" && h.Crc == "ded53c64" && h.Dump == "Mega Man X (USA) (Rev 1)");
    }

    [Fact]
    public void Le_passeport_ne_porte_l_empreinte_sans_en_tete_que_du_contenu_que_le_wrapper_a_mesure()
    {
        var rom = Ecrire("Super Mario Bros. 3 (USA) (Rev 1).nes", Nes2);
        Assert.Equal(Md5(Corps), NelfePlayScoringReporter.SansEnTeteMesure(rom, "nes", Md5(Nes2).ToUpperInvariant()));
        // Le wrapper a mesure autre chose : rien ne lie ce fichier a la partie.
        Assert.Null(NelfePlayScoringReporter.SansEnTeteMesure(rom, "nes", Md5(Ines)));
        Assert.Null(NelfePlayScoringReporter.SansEnTeteMesure(rom, "arcade", Md5(Nes2)));
        Assert.Null(NelfePlayScoringReporter.SansEnTeteMesure(rom, "megadrive", Md5(Nes2)));
        Assert.Null(NelfePlayScoringReporter.SansEnTeteMesure(null, "nes", Md5(Nes2)));

        var artefact = NelfePlayScoringReporter.ContentArtifact(null, Md5(Nes2), null, Md5(Corps));
        Assert.Equal(Md5(Nes2), (string?)artefact["md5"]);
        Assert.Equal(Md5(Corps), (string?)artefact["rhash_md5"]);
        Assert.False(NelfePlayScoringReporter.ContentArtifact(null, Md5(Nes2), null).ContainsKey("rhash_md5"));
    }
}
