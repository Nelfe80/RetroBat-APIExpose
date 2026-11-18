using System.Security.Cryptography;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'IDENTITE D'UN JEU CONSOLE PAR SON CONTENU (B, 18/11) : le md5 connu d'alias.json garde la reponse du wrapper ; sinon le
/// referentiel des gamelists, sans en-tete d'abord, quand le groupe a son .MEM ; sinon rien (le nom, comme avant).
/// </summary>
public sealed class IdentiteDesJeuxTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "nelfe-identite-" + Guid.NewGuid().ToString("N"));
    private readonly string _ram;
    private readonly IdentiteDesJeux _identite;

    // La ROM d'essai : un en-tete iNES et 32 Kio de contenu.
    private static readonly byte[] Contenu = Enumerable.Range(0, 32 * 1024).Select(i => (byte)(i * 7 % 251)).ToArray();
    private static readonly byte[] EnTete = [0x4E, 0x45, 0x53, 0x1A, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly string Md5SansEnTete = Convert.ToHexString(MD5.HashData(Contenu)).ToLowerInvariant();
    private static readonly string Md5DuFichier = Convert.ToHexString(MD5.HashData(EnTete.Concat(Contenu).ToArray())).ToLowerInvariant();

    public IdentiteDesJeuxTests()
    {
        _ram = Path.Combine(_dossier, "ram");
        Directory.CreateDirectory(Path.Combine(_ram, "nes"));
        var referentiel = Path.Combine(_dossier, "systems");
        Directory.CreateDirectory(referentiel);
        File.WriteAllLines(Path.Combine(referentiel, "nes_lt.json"), new[]
        {
            // Le dump No-Intro : empreinte sans en-tete, groupe avec .MEM.
            "{\"hsh\": [{\"md5\": \"" + Md5SansEnTete + "\"}], \"n\": \"Jeu d'essai\", \"sys\": \"nes\", \"t\": \"game\", \"id\": \"jeu_essai_usa\", \"grp\": \"jeu_d_essai\", \"fn\": \"Jeu d'essai (USA)\"}",
            // Un groupe connu du referentiel mais sans .MEM.
            "{\"hsh\": [{\"md5\": \"11111111111111111111111111111111\"}], \"n\": \"Sans definition\", \"sys\": \"nes\", \"t\": \"game\", \"id\": \"sans_def\", \"grp\": \"sans_definition\", \"fn\": \"Sans definition (USA)\"}",
        });
        File.WriteAllText(Path.Combine(_ram, "nes", "jeu-d-essai.MEM"), "address=0x0000, size=1, event=score\n");
        File.WriteAllText(Path.Combine(_ram, "nes", "autre-jeu.MEM"), "address=0x0000, size=1, event=score\n");
        File.WriteAllText(Path.Combine(_ram, "nes", "alias.json"),
            "{\n  \"Jeu d'essai\": \"jeu-d-essai\",\n  \"22222222222222222222222222222222\": \"autre-jeu\",\n  \"33333333333333333333333333333333\": \"absent\"\n}\n");
        _identite = new IdentiteDesJeux(new RomCanonicalResolver(racineDuReferentiel: referentiel), _ram);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Un_md5_connu_d_alias_json_garde_la_reponse_du_wrapper_sans_rien_lui_imposer()
    {
        var id = _identite.Decider("nes", "22222222222222222222222222222222", Md5SansEnTete);

        Assert.NotNull(id);
        Assert.Equal("autre-jeu", id!.Mem);
        Assert.Equal(IdentiteDesJeux.Voie.AliasParEmpreinte, id.Voie);
        Assert.False(id.AImposerAuWrapper);
    }

    [Fact]
    public void Un_md5_d_alias_json_dont_le_mem_manque_ne_donne_rien()
        => Assert.Null(_identite.Decider("nes", "33333333333333333333333333333333", Md5SansEnTete));

    [Fact]
    public void Un_dump_inconnu_d_alias_json_est_reconnu_par_son_empreinte_sans_en_tete_et_impose_au_wrapper()
    {
        var id = _identite.Decider("nes", "44444444444444444444444444444444", Md5SansEnTete);

        Assert.NotNull(id);
        Assert.Equal("jeu-d-essai", id!.Mem);
        Assert.Equal(IdentiteDesJeux.Voie.Referentiel, id.Voie);
        Assert.True(id.AImposerAuWrapper);
    }

    [Fact]
    public void Un_groupe_du_referentiel_sans_mem_ne_donne_rien()
        => Assert.Null(_identite.Decider("nes", "11111111111111111111111111111111", null));

    [Fact]
    public void L_arcade_garde_son_set()
        => Assert.Null(_identite.Decider("arcade", "22222222222222222222222222222222", Md5SansEnTete));

    [Fact]
    public void La_rom_sur_le_disque_est_lue_une_fois_et_reconnue_quel_que_soit_son_nom()
    {
        var roms = Path.Combine(_dossier, "roms", "nes");
        Directory.CreateDirectory(roms);
        var chemin = Path.Combine(roms, "Jeu Essai (U) (PRG 0).nes");
        File.WriteAllBytes(chemin, EnTete.Concat(Contenu).ToArray());

        // Le systeme vient du dossier sous roms\, pas de celui que le menu d'ES nomme (une collection).
        var id = _identite.Pour("auto-allgames", chemin);

        Assert.NotNull(id);
        Assert.Equal("jeu-d-essai", id!.Mem);
        Assert.Equal(Md5DuFichier, id.Md5);
        Assert.Equal(Md5SansEnTete, id.SansEnTete);
        Assert.True(id.AImposerAuWrapper);
    }

    [Fact]
    public void Un_fichier_absent_ne_donne_rien()
        => Assert.Null(_identite.Pour("nes", Path.Combine(_dossier, "roms", "nes", "absent.nes")));

    [Theory]
    [InlineData("super-mario-bros-3", true)]
    [InlineData("z-hack_mario-2", true)]
    [InlineData("Super-Mario", false)]
    [InlineData("..\\arcade\\mslug", false)]
    [InlineData("a/b", false)]
    [InlineData("a.b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Un_nom_de_mem_ecrit_pour_le_wrapper_n_est_jamais_un_chemin(string? nom, bool sur)
        => Assert.Equal(sur, IdentiteDesJeux.NomDeMemSur(nom));

    [Theory]
    [InlineData(@"E:\RetroBat\roms\nes\Jeu.nes", "nes")]
    [InlineData(@"E:\RetroBat\roms\snes\USA\Jeu.sfc", "snes")]
    [InlineData("E:/RetroBat/roms/gbc/Jeu.zip", "gbc")]
    [InlineData(@"D:\ailleurs\Jeu.nes", null)]
    public void Le_systeme_est_le_dossier_qui_suit_roms(string chemin, string? attendu)
        => Assert.Equal(attendu, IdentiteDesJeux.SystemeDuChemin(chemin));
}
