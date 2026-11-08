using System.IO.Compression;
using System.Security.Cryptography;
using RetroBat.Api.MassRun;
using RetroBat.Api.Speedrun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES FANTOMES D'UN DUMP SOUS UN AUTRE EN-TETE (08/11, suite du support de neogeoff) : le No-Intro (NES 2.0) de Super Mario
/// Bros. 3 ou de Mega Man 2 part sur le coeur hote quand son md5 sans en-tete est celui du dump classe, et un fantome pose dans
/// le dossier du jeu y porte toujours le crc du dump classe, quel que soit le fichier sur lequel il a ete fait.
/// </summary>
public sealed class FantomesSansEnTeteTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "nelfe-fantomes-en-tete-" + Guid.NewGuid().ToString("N"));

    public FantomesSansEnTeteTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
    }

    private static string Md5(ReadOnlySpan<byte> octets) => Convert.ToHexString(MD5.HashData(octets)).ToLowerInvariant();

    private static byte[] Corps(int graine)
    {
        var corps = new byte[2 * 16384 + 8192];
        new Random(graine).NextBytes(corps);
        return corps;
    }

    private static readonly byte[] CorpsClasse = Corps(5);

    /// <summary>Le dump classe (GoodNES, iNES 1.0) et le No-Intro (NES 2.0) : meme corps, autre en-tete.</summary>
    private static byte[] Ines(byte[] corps) => [0x4E, 0x45, 0x53, 0x1A, 0x02, 0x01, 0x40, 0, 0, 0, 0, 0, 0, 0, 0, 0, .. corps];

    private static byte[] Nes2(byte[] corps) => [0x4E, 0x45, 0x53, 0x1A, 0x02, 0x01, 0x40, 0x08, 0, 0, 0x07, 0, 0, 0, 0, 0x01, .. corps];

    private static string Crc(byte[] octets) => JeuxMassRun.Crc32(octets).ToString("x8");

    private static FantomesDuSpeedrun.Hote Hote(string? sansEnTete) => new("nes", "jeu-d-essai", "fceumm", "fceumm_ghosts",
        Crc(Ines(CorpsClasse)), true, "Jeu d'essai (USA)", sansEnTete);

    private string Ecrire(string nom, byte[] octets)
    {
        var chemin = Path.Combine(_dossier, nom);
        File.WriteAllBytes(chemin, octets);
        return chemin;
    }

    [Fact]
    public void Le_dump_classe_part_sur_le_coeur_hote_comme_avant()
    {
        var rom = Ecrire("classe.nes", Ines(CorpsClasse));
        Assert.True(FantomesDuSpeedrun.ContenuDuHote(Hote(null), rom));
        Assert.True(FantomesDuSpeedrun.ContenuDuHote(Hote(Md5(CorpsClasse)), rom));
    }

    [Fact]
    public void Le_no_intro_part_sur_le_coeur_hote_par_son_md5_sans_en_tete()
    {
        var rom = Ecrire("no-intro.nes", Nes2(CorpsClasse));
        Assert.True(FantomesDuSpeedrun.ContenuDuHote(Hote(Md5(CorpsClasse)), rom));
        Assert.True(FantomesDuSpeedrun.ContenuDuHote(Hote(Md5(CorpsClasse).ToUpperInvariant()), rom));
    }

    [Fact]
    public void Le_no_intro_reste_sur_le_coeur_d_origine_si_le_coeur_hote_ne_sait_pas_le_reconnaitre()
    {
        // pas d'empreinte sans en-tete pour ce jeu : le coeur hote ne connait que le crc du fichier entier (comme avant le 08/11)
        Assert.False(FantomesDuSpeedrun.ContenuDuHote(Hote(null), Ecrire("no-intro.nes", Nes2(CorpsClasse))));
        Assert.False(FantomesDuSpeedrun.ContenuDuHote(Hote(""), Ecrire("no-intro-2.nes", Nes2(CorpsClasse))));
    }

    [Fact]
    public void Un_autre_jeu_reste_sur_le_coeur_d_origine()
    {
        Assert.False(FantomesDuSpeedrun.ContenuDuHote(Hote(Md5(CorpsClasse)), Ecrire("autre.nes", Nes2(Corps(6)))));
        Assert.False(FantomesDuSpeedrun.ContenuDuHote(Hote(Md5(CorpsClasse)), Ecrire("autre-ines.nes", Ines(Corps(6)))));
    }

    [Fact]
    public void Le_no_intro_dans_un_zip_part_aussi_sur_le_coeur_hote()
    {
        var zip = Path.Combine(_dossier, "no-intro.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var flux = archive.CreateEntry("Jeu d'essai (USA).nes").Open();
            flux.Write(Nes2(CorpsClasse));
        }
        Assert.True(FantomesDuSpeedrun.ContenuDuHote(Hote(Md5(CorpsClasse)), zip));
    }

    [Fact]
    public void Un_fichier_illisible_ne_bloque_pas_le_coeur_hote()
        => Assert.True(FantomesDuSpeedrun.ContenuDuHote(Hote(Md5(CorpsClasse)), Path.Combine(_dossier, "absent.nes")));

    [Fact]
    public void Un_fantome_fait_sur_le_no_intro_prend_le_crc_du_dump_classe()
    {
        // NGHOST1\0, crc du No-Intro entier, decalage 7, etat de 3 octets, 2 images
        var f = new List<byte>("NGHOST1\0"u8.ToArray());
        f.AddRange(BitConverter.GetBytes(0xE8C3AF69u));
        f.AddRange(BitConverter.GetBytes(7));
        f.AddRange(BitConverter.GetBytes(3u));
        f.AddRange(new byte[] { 1, 2, 3 });
        f.AddRange(BitConverter.GetBytes(2u));
        var fantome = f.ToArray();

        var pose = FantomesDuSpeedrun.AuCrcDuJeu(fantome, "0b742b33");
        Assert.Equal(0x0B742B33u, BitConverter.ToUInt32(pose, 8));
        // le reste ne bouge pas, et l'original non plus
        Assert.Equal(fantome.Length, pose.Length);
        Assert.True(pose.AsSpan(12).SequenceEqual(fantome.AsSpan(12)));
        Assert.True(pose.AsSpan(0, 8).SequenceEqual(fantome.AsSpan(0, 8)));
        Assert.Equal(0xE8C3AF69u, BitConverter.ToUInt32(fantome, 8));
        Assert.Equal(2, FantomesDuSpeedrun.ImagesDuFantome(pose));
        // deja au bon crc : identique
        Assert.Equal(pose, FantomesDuSpeedrun.AuCrcDuJeu(pose, "0b742b33"));
    }

    [Fact]
    public void Ce_qui_n_est_pas_un_fantome_ne_se_reecrit_pas()
    {
        var rien = "PAS UN FANTOME DU TOUT"u8.ToArray();
        Assert.Same(rien, FantomesDuSpeedrun.AuCrcDuJeu(rien, "0b742b33"));
        var court = "NGHOST1\0"u8.ToArray();
        Assert.Same(court, FantomesDuSpeedrun.AuCrcDuJeu(court, "0b742b33"));
        var fantome = "NGHOST1\0\x01\x02\x03\x04"u8.ToArray();
        Assert.Same(fantome, FantomesDuSpeedrun.AuCrcDuJeu(fantome, "pas-un-crc"));
    }

    [Fact]
    public void Les_jeux_NES_a_coeur_hote_publient_le_md5_sans_en_tete_de_leur_dump_classe()
    {
        var hotes = FantomesDuSpeedrun.Liste;
        // les md5 sans en-tete du referentiel (GoodNES de la borne et No-Intro concordent, rapport identite_rhash du 08/11)
        Assert.Equal("55b7111567c1709e849c574078699577", hotes.Single(h => h.Groupe == "super-mario-bros-3").SansEnTete);
        Assert.Equal("0527a0ee512f69e08b8db6dc97964632", hotes.Single(h => h.Groupe == "mega-man-2").SansEnTete);
        Assert.Equal("8e3630186e35d477231bf8fd50e54cdd", hotes.Single(h => h.Groupe == "super-mario-bros").SansEnTete);
        // le coeur hote SNES et Mega Drive ne connaissent que le fichier entier
        Assert.All(hotes.Where(h => h.Systeme != "nes"), h => Assert.Null(h.SansEnTete));
        Assert.All(hotes.Where(h => h.SansEnTete is not null), h => Assert.Matches("^[0-9a-f]{32}$", h.SansEnTete!));
    }
}
