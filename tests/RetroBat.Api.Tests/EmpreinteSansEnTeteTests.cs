using System.Security.Cryptography;
using System.Text;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les regles d'empreinte sans en-tete, console par console, contre celles de rcheevos (src/hash/rc_hash_rom.c, commit
/// eea6461f). Chaque fixture est construite en memoire : un corps de ROM arbitraire, puis l'en-tete que la regle doit ignorer.
/// </summary>
public sealed class EmpreinteSansEnTeteTests
{
    private static byte[] Corps(int taille, int graine = 7)
    {
        var octets = new byte[taille];
        new Random(graine).NextBytes(octets);
        return octets;
    }

    private static string Md5(ReadOnlySpan<byte> octets) => Convert.ToHexString(MD5.HashData(octets)).ToLowerInvariant();

    private static byte[] Avec(byte[] tete, byte[] corps) => [.. tete, .. corps];

    private static byte[] En16(string marque, params byte[] suite)
    {
        var tete = new byte[16];
        Encoding.Latin1.GetBytes(marque).CopyTo(tete, 0);
        suite.CopyTo(tete, 4);
        return tete;
    }

    [Theory]
    [InlineData("nes", RegleSansEnTete.Nes)]
    [InlineData("FDS", RegleSansEnTete.Nes)]
    [InlineData("famicom", RegleSansEnTete.Nes)]
    [InlineData("snes", RegleSansEnTete.SuperNes)]
    [InlineData("sfc", RegleSansEnTete.SuperNes)]
    [InlineData("pcengine", RegleSansEnTete.PcEngine)]
    [InlineData("supergrafx", RegleSansEnTete.PcEngine)]
    [InlineData("lynx", RegleSansEnTete.Lynx)]
    [InlineData("atari7800", RegleSansEnTete.Atari7800)]
    [InlineData("n64", RegleSansEnTete.Nintendo64)]
    [InlineData("scv", RegleSansEnTete.SuperCassetteVision)]
    [InlineData("megadrive", RegleSansEnTete.Aucune)]
    [InlineData("mastersystem", RegleSansEnTete.Aucune)]
    [InlineData("gb", RegleSansEnTete.Aucune)]
    [InlineData("gba", RegleSansEnTete.Aucune)]
    [InlineData("arcade", RegleSansEnTete.Aucune)]
    [InlineData("fbneo", RegleSansEnTete.Aucune)]
    [InlineData("mame", RegleSansEnTete.Aucune)]
    [InlineData(null, RegleSansEnTete.Aucune)]
    public void Chaque_systeme_a_sa_regle_et_l_arcade_n_en_a_aucune(string? systeme, RegleSansEnTete attendue)
        => Assert.Equal(attendue, EmpreinteSansEnTete.RegleDu(systeme));

    [Fact]
    public void Nes_le_meme_jeu_sous_ines_et_sous_nes2_a_la_meme_empreinte()
    {
        // SMB3 (USA) (Rev 1) : GoodNES en iNES 1.0, No-Intro en NES 2.0 ; meme corps (PRG + CHR).
        var corps = Corps(16 * 16384 + 16 * 8192);
        var ines = Avec(En16("NES\x1a", 0x10, 0x10, 0x40), corps);
        var nes2 = Avec(En16("NES\x1a", 0x10, 0x10, 0x40, 0x08, 0, 0, 0x07), corps);

        Assert.NotEqual(Md5(ines), Md5(nes2));
        Assert.Equal(Md5(corps), EmpreinteSansEnTete.Md5(RegleSansEnTete.Nes, ines));
        Assert.Equal(Md5(corps), EmpreinteSansEnTete.Md5(RegleSansEnTete.Nes, nes2));
    }

    [Fact]
    public void Nes_et_fds_l_en_tete_fds_s_ignore_aussi_et_un_fichier_sans_en_tete_garde_son_md5()
    {
        var corps = Corps(65500);
        Assert.Equal(Md5(corps), EmpreinteSansEnTete.Md5(RegleSansEnTete.Nes, Avec(En16("FDS\x1a", 1), corps)));
        Assert.Equal(Md5(corps), EmpreinteSansEnTete.Md5(RegleSansEnTete.Nes, corps));
    }

    [Fact]
    public void Nes_un_fichier_de_16_octets_ou_moins_n_a_pas_d_en_tete_a_enlever()
    {
        // rcheevos : buffer_size > 16.
        var seize = En16("NES\x1a", 1, 1);
        Assert.Equal(Md5(seize), EmpreinteSansEnTete.Md5(RegleSansEnTete.Nes, seize));
    }

    [Fact]
    public void SuperNes_l_en_tete_de_copieur_s_enleve_quand_la_taille_vaut_512_de_plus_qu_un_multiple_de_8_kio()
    {
        var corps = Corps(4 * 0x2000);
        var copieur = Avec(new byte[512], corps);
        Assert.Equal(Md5(corps), EmpreinteSansEnTete.Md5(RegleSansEnTete.SuperNes, copieur));
        // Sans en-tete, le md5 du fichier.
        Assert.Equal(Md5(corps), EmpreinteSansEnTete.Md5(RegleSansEnTete.SuperNes, corps));
    }

    [Fact]
    public void SuperNes_la_regle_est_le_multiple_de_8_kio_de_rcheevos_et_pas_de_1_kio()
    {
        // 1 Kio + 512 : 1536 % 1024 == 512, mais 1536 - 0 != 512 au sens de rcheevos (multiple de 0x2000) : rien ne s'enleve.
        var fichier = Corps(1536);
        Assert.Equal(Md5(fichier), EmpreinteSansEnTete.Md5(RegleSansEnTete.SuperNes, fichier));
        var autre = Corps(0x2000 + 1024 + 512);
        Assert.Equal(Md5(autre), EmpreinteSansEnTete.Md5(RegleSansEnTete.SuperNes, autre));
    }

    [Fact]
    public void PcEngine_512_octets_s_enlevent_des_que_le_bit_512_de_la_taille_est_pose()
    {
        var corps = Corps(0x40000);
        Assert.Equal(Md5(corps), EmpreinteSansEnTete.Md5(RegleSansEnTete.PcEngine, Avec(Corps(512, 3), corps)));
        Assert.Equal(Md5(corps), EmpreinteSansEnTete.Md5(RegleSansEnTete.PcEngine, corps));
        // rcheevos teste le bit (taille & 512), pas un reste : 0x40000 + 1024 + 512 perd aussi 512 octets.
        var bizarre = Corps(0x40000 + 1536, 5);
        Assert.Equal(Md5(bizarre.AsSpan(512)), EmpreinteSansEnTete.Md5(RegleSansEnTete.PcEngine, bizarre));
    }

    [Fact]
    public void Lynx_l_en_tete_lynx_de_64_octets_s_enleve()
    {
        var corps = Corps(0x20000);
        var tete = new byte[64];
        "LYNX\0"u8.CopyTo(tete);
        Assert.Equal(Md5(corps), EmpreinteSansEnTete.Md5(RegleSansEnTete.Lynx, Avec(tete, corps)));
        Assert.Equal(Md5(corps), EmpreinteSansEnTete.Md5(RegleSansEnTete.Lynx, corps));
        // « LYNX » suivi d'autre chose que 0 n'est pas l'en-tete (rcheevos compare 5 octets).
        var presque = (byte[])tete.Clone();
        presque[4] = (byte)'!';
        var faux = Avec(presque, corps);
        Assert.Equal(Md5(faux), EmpreinteSansEnTete.Md5(RegleSansEnTete.Lynx, faux));
    }

    [Fact]
    public void Atari7800_l_en_tete_atari7800_de_128_octets_s_enleve()
    {
        var corps = Corps(0x8000);
        var tete = new byte[128];
        tete[0] = 1;
        "ATARI7800"u8.CopyTo(tete.AsSpan(1));
        Assert.Equal(Md5(corps), EmpreinteSansEnTete.Md5(RegleSansEnTete.Atari7800, Avec(tete, corps)));
        Assert.Equal(Md5(corps), EmpreinteSansEnTete.Md5(RegleSansEnTete.Atari7800, corps));
    }

    [Fact]
    public void SuperCassetteVision_l_en_tete_emuscv_de_32_octets_s_enleve()
    {
        var corps = Corps(0x8000);
        var tete = new byte[32];
        "EmuSCV"u8.CopyTo(tete);
        Assert.Equal(Md5(corps), EmpreinteSansEnTete.Md5(RegleSansEnTete.SuperCassetteVision, Avec(tete, corps)));
    }

    private static byte[] Z64(int taille)
    {
        var rom = Corps(taille, 11);
        rom[0] = 0x80;
        rom[1] = 0x37;
        rom[2] = 0x12;
        rom[3] = 0x40;
        return rom;
    }

    [Fact]
    public void Nintendo64_v64_et_n64_se_remettent_dans_l_ordre_z64()
    {
        var z64 = Z64(0x30000);
        var v64 = new byte[z64.Length];
        var n64 = new byte[z64.Length];
        for (var i = 0; i < z64.Length; i += 4)
        {
            v64[i] = z64[i + 1]; v64[i + 1] = z64[i]; v64[i + 2] = z64[i + 3]; v64[i + 3] = z64[i + 2];
            n64[i] = z64[i + 3]; n64[i + 1] = z64[i + 2]; n64[i + 2] = z64[i + 1]; n64[i + 3] = z64[i];
        }
        Assert.Equal(0x37, v64[0]);
        Assert.Equal(0x40, n64[0]);

        Assert.Equal(Md5(z64), EmpreinteSansEnTete.Md5(RegleSansEnTete.Nintendo64, z64));
        Assert.Equal(Md5(z64), EmpreinteSansEnTete.Md5(RegleSansEnTete.Nintendo64, v64));
        Assert.Equal(Md5(z64), EmpreinteSansEnTete.Md5(RegleSansEnTete.Nintendo64, n64));
    }

    [Fact]
    public void Nintendo64_un_disque_ndd_reste_tel_quel_et_un_premier_octet_inconnu_n_a_pas_d_empreinte()
    {
        var ndd = Corps(0x1000);
        ndd[0] = 0xE8;
        Assert.Equal(Md5(ndd), EmpreinteSansEnTete.Md5(RegleSansEnTete.Nintendo64, ndd));
        var autre = Corps(0x1000);
        autre[0] = 0x12;
        Assert.Null(EmpreinteSansEnTete.Md5(RegleSansEnTete.Nintendo64, autre));
    }

    [Fact]
    public void Sans_regle_pas_d_empreinte_sans_en_tete_mais_le_md5_du_fichier_reste()
    {
        var rom = Corps(0x8000);
        Assert.Null(EmpreinteSansEnTete.Md5(RegleSansEnTete.Aucune, rom));
        using var flux = new MemoryStream(rom);
        var (fichier, sansEnTete) = EmpreinteSansEnTete.Empreintes(RegleSansEnTete.Aucune, flux, rom.Length);
        Assert.Equal(Md5(rom), fichier);
        Assert.Null(sansEnTete);
    }

    [Fact]
    public void Une_seule_lecture_donne_le_md5_du_fichier_et_celui_sans_en_tete()
    {
        var corps = Corps(40960);
        var ines = Avec(En16("NES\x1a", 2, 1), corps);
        using var flux = new MemoryStream(ines);
        var (fichier, sansEnTete) = EmpreinteSansEnTete.Empreintes(RegleSansEnTete.Nes, flux, ines.Length);
        Assert.Equal(Md5(ines), fichier);
        Assert.Equal(Md5(corps), sansEnTete);
    }

    [Fact]
    public void Un_flux_qui_ment_sur_sa_taille_ne_donne_rien()
    {
        var rom = Corps(5000);
        using (var court = new MemoryStream(rom)) Assert.Equal((null, null), EmpreinteSansEnTete.Empreintes(RegleSansEnTete.Nes, court, rom.Length + 10));
        using (var long_ = new MemoryStream(rom)) Assert.Equal((null, null), EmpreinteSansEnTete.Empreintes(RegleSansEnTete.Nes, long_, rom.Length - 10));
    }

    /// <summary>Un flux de zeros d'une taille donnee, sans rien allouer.</summary>
    private sealed class Zeros(long taille) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => taille;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, taille - _position);
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void Comme_rcheevos_seuls_les_64_premiers_mio_comptent()
    {
        var taille = EmpreinteSansEnTete.FenetreMaximale + 0x2000 + 512;
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var zeros = new byte[1 << 20];
        // Fenetre de 64 Mio, puis la regle SNES sur cette fenetre : 64 Mio est un multiple de 8 Kio, rien ne s'enleve.
        for (long fait = 0; fait < EmpreinteSansEnTete.FenetreMaximale; fait += zeros.Length) md5.AppendData(zeros);
        var attendu = Convert.ToHexString(md5.GetHashAndReset()).ToLowerInvariant();

        var (fichier, sansEnTete) = EmpreinteSansEnTete.Empreintes(RegleSansEnTete.SuperNes, new Zeros(taille), taille);
        Assert.Equal(attendu, sansEnTete);
        Assert.NotEqual(attendu, fichier);
    }
}
