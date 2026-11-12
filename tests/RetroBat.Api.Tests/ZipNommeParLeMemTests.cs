using RetroBat.Api.Infrastructure;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// (12/11) LE ZIP PUBLIE D'UN SET NOMME PAR LE .MEM. Batsugun Special, Dangun Feveron, Gradius II et RayForce etaient ecartes de la
/// collection (« ROM non reconnue ») : le site n'y publie que le md5 et le sha1 du zip appris d'une partie (le « grp » de leur
/// referentiel n'est pas leur rom_group, donc ni md5 du nom de set ni sha1 declare), et le zip ne valait que s'il etait lui-meme
/// nomme par le .MEM. Le set est nomme (par le md5 de son nom, comme tous les .MEM d'arcade) : son zip publie le confirme.
/// </summary>
public class ZipNommeParLeMemTests
{
    private static OpenGame Arcade(string groupe, string[] md5, string[] sha1)
        => new() { SystemId = "arcade", RomGroup = groupe, ContentHashes = new ContentHashes { Md5 = md5, Sha1 = sha1 } };

    private static InstalledGame Set(string groupe, string set, string? mem = null) => new(
        "fbneo", "arcade", groupe, $"E:/RetroBat/roms/fbneo/{set}.zip", set, null, null, true, mem);

    private static string MemNommant(string nom, params string[] sets)
    {
        var chemin = Path.Combine(Path.GetTempPath(), "zip-nomme-" + nom + ".MEM");
        var lignes = string.Join("\n", sets.Select(s =>
            $" {{ hash = \"{NelfePlayScoringCollectionSyncService.Md5DuNomDeSet(s)}\", label = \"{s}.7z <{s}>\" }},"));
        File.WriteAllText(chemin, "rom = { hashes = {\n" + lignes + "\n } }");
        return chemin;
    }

    private static bool Confirme(InstalledGame c, OpenGame jeu, string? md5Zip, Func<string?, string?, string?, string?>? sha1DuSet = null,
        Func<string?, string?, bool>? bootleg = null, Func<string, bool>? clone = null)
        => NelfePlayScoringCollectionSyncService.ContenuConfirme(c, jeu, sha1DuSet ?? ((_, _, _) => null), _ => null,
            bootlegOuHack: bootleg, md5DuFichier: _ => md5Zip, cloneOfficiel: clone ?? (_ => false));

    [Theory]
    [InlineData("batsugun-special", "batsugunsp", "57316d77bede660bcb7efe067a317636", "7b3db8e93ede62eb8fec492fad99583b63bdeb0f")]
    [InlineData("dangun-feveron", "dfeveron", "5e89cc3903a103abf4d3a3bdcb5068ed", "ba4beb9ee588bd88b87f34a3702d3e1497d5a8ba")]
    [InlineData("gradius-ii-gofer-no-yabou", "gradius2", "3b9d342a4b734e52867f36496589290e", "c7fafe1e9f923f0ff55a4c3c86dc7ef1e76b750e")]
    [InlineData("rayforce", "rayforce", "72d770c3144f716a8061d4d130cb84b7", "4ded3ed0f06d813b92d2202489787749b480cb9d")]
    public void Les_4_jeux_de_la_borne_zip_publie_et_set_nomme_sont_admis(string groupe, string set, string md5Zip, string sha1Zip)
    {
        var jeu = Arcade(groupe, [md5Zip], [sha1Zip]);
        var mem = MemNommant(groupe, set);
        // Le sha1 declare du set (gamelist) n'est PAS publie : seul le zip l'est.
        Assert.True(Confirme(Set(groupe, set, mem), jeu, md5Zip, (_, _, _) => "3a0a9cdf894926a16800c4882a2b00383d981367"));
    }

    [Fact]
    public void Un_zip_au_md5_non_publie_reste_refuse_meme_si_le_set_est_nomme()
    {
        var jeu = Arcade("batsugun-special", ["57316d77bede660bcb7efe067a317636"], ["7b3db8e93ede62eb8fec492fad99583b63bdeb0f"]);
        var mem = MemNommant("bs2", "batsugunsp");
        Assert.False(Confirme(Set("batsugun-special", "batsugunsp", mem), jeu, "0123456789abcdef0123456789abcdef", (_, _, _) => "3a0a9cdf894926a16800c4882a2b00383d981367"));
    }

    [Fact]
    public void Un_zip_publie_dont_le_set_n_est_pas_nomme_reste_refuse_sauf_clone_officiel()
    {
        var jeu = Arcade("rayforce", ["72d770c3144f716a8061d4d130cb84b7"], ["4ded3ed0f06d813b92d2202489787749b480cb9d"]);
        var mem = MemNommant("rf2", "rayforcej"); // le .MEM ne nomme PAS « rayforce »
        var autre = Set("rayforce", "rayforce", mem);
        Assert.False(Confirme(autre, jeu, "72d770c3144f716a8061d4d130cb84b7", clone: _ => false));
        // La voie du clone officiel ne change pas.
        Assert.True(Confirme(autre, jeu, "72d770c3144f716a8061d4d130cb84b7", clone: _ => true));
    }

    [Fact]
    public void Un_bootleg_nomme_au_zip_publie_reste_refuse()
    {
        var jeu = Arcade("ms-pac-man", ["f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1"], ["5e8b472b615f12efca3fe792410c23619f067845"]);
        var mem = MemNommant("mspacmab", "mspacmab");
        Assert.False(Confirme(Set("ms-pac-man", "mspacmab", mem), jeu, "f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1f1", bootleg: (_, set) => set == "mspacmab"));
    }

    [Fact]
    public void Un_sha1_partage_du_BIOS_Neo_Geo_ne_fait_pas_entrer_un_autre_jeu()
    {
        const string bios = "5992277debadeb64d1c1c64b0a92d9293eaf7e4a";
        var jeu = Arcade("double-dragon", [NelfePlayScoringCollectionSyncService.Md5DuNomDeSet("ddragon")], ["1504faaf07c541330cd43b72dc6846911dfd85a3", bios]);
        // doubledr n'est pas nomme et son zip n'est pas publie : le sha1 du BIOS ne le fait pas entrer.
        Assert.False(Confirme(Set("double-dragon", "doubledr"), jeu, "0123456789abcdef0123456789abcdef", (_, _, _) => bios));
    }

    [Fact]
    public void Le_zip_n_est_pas_relu_le_md5_calcule_une_fois_sert_aux_deux_tests()
    {
        var jeu = Arcade("dangun-feveron", ["5e89cc3903a103abf4d3a3bdcb5068ed"], ["ba4beb9ee588bd88b87f34a3702d3e1497d5a8ba"]);
        var mem = MemNommant("df", "dfeveron");
        var lectures = 0;
        var ok = NelfePlayScoringCollectionSyncService.ContenuConfirme(Set("dangun-feveron", "dfeveron", mem), jeu, (_, _, _) => null, _ => null,
            md5DuFichier: _ => { lectures++; return "5e89cc3903a103abf4d3a3bdcb5068ed"; });
        Assert.True(ok);
        Assert.Equal(1, lectures);
    }
}
