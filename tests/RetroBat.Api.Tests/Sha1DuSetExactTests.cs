using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LE SHA1 DECLARE VIENT DU SET EXACT (15/11, audit des ROMs parent) : la recherche s'arretait a la premiere ligne trouvee
/// par le set OU par le groupe. Progear tombait sur « halfway » (meme groupe, sans empreinte) et declarait un sha1 vide ;
/// Rygar prenait celui de « pc_rygar » (PlayChoice), place avant lui dans le referentiel.
/// </summary>
public sealed class Sha1DuSetExactTests : IDisposable
{
    private readonly string _racine = Directory.CreateTempSubdirectory("referentiels-").FullName;

    public Sha1DuSetExactTests()
    {
        File.WriteAllLines(Path.Combine(_racine, "arcade_lt.json"),
        [
            """{"set":"halfway","grp":"Progear","n":"Halfway To Hell: Progear Red Label"}""",
            """{"set":"pc_rygar","grp":"Rygar","n":"Rygar (PlayChoice-10)","hsh":[{"sha1":"0ad386fc0000000000000000000000000000000a"}]}""",
            """{"set":"rygar","grp":"Rygar","n":"Rygar (US set 1)","hsh":[{"sha1":"656e29c890f5de964920b7841b3e11469cd20051"}]}""",
            """{"set":"progear","grp":"Progear","n":"Progear (010117 Europe)","hsh":[{"sha1":"afbb26fed6cd0cb5c0099a10d35aeb453318c14d"}]}""",
        ]);
        File.WriteAllLines(Path.Combine(_racine, "fbneo_lt.json"),
        [
            """{"set":"progearj","grp":"Progear","n":"Progear No Arashi (010117 Japan)","hsh":[{"sha1":"1111111111111111111111111111111111111111"}]}""",
        ]);
    }

    public void Dispose()
    {
        try { Directory.Delete(_racine, true); } catch { }
    }

    private string? Sha1(string slug, string set)
        => GamelistIdentity.ChercherDansLesSystemes(_racine, ["arcade", "fbneo"], slug, set, GamelistIdentity.PremierSha1, groupeSiSet: true);

    [Fact]
    public void Progear_prend_l_empreinte_de_son_set_et_pas_la_ligne_vide_de_halfway()
        => Assert.Equal("afbb26fed6cd0cb5c0099a10d35aeb453318c14d", Sha1("progear", "progear"));

    [Fact]
    public void Rygar_prend_l_empreinte_de_son_set_et_pas_celle_de_la_version_PlayChoice()
        => Assert.Equal("656e29c890f5de964920b7841b3e11469cd20051", Sha1("rygar", "rygar"));

    [Fact]
    public void Un_set_d_un_autre_referentiel_est_trouve_avant_tout_repli_sur_le_groupe()
        => Assert.Equal("1111111111111111111111111111111111111111", Sha1("progear", "progearj"));

    [Fact]
    public void Un_set_absent_des_referentiels_garde_le_repli_du_groupe_mais_jamais_une_ligne_vide()
        => Assert.Equal("afbb26fed6cd0cb5c0099a10d35aeb453318c14d", Sha1("progear", "progear_inconnu"));

    [Fact]
    public void Sans_set_le_groupe_donne_la_premiere_ligne_qui_a_une_empreinte()
        => Assert.Equal("afbb26fed6cd0cb5c0099a10d35aeb453318c14d", Sha1("progear", ""));

    [Fact]
    public void Le_nom_du_jeu_vient_du_set_exact_meme_avec_un_repli_de_groupe_permis()
        => Assert.Equal("Progear (010117 Europe)",
            GamelistIdentity.ChercherDansLesSystemes(_racine, ["arcade", "fbneo"], "progear", "progear", GamelistIdentity.Nom, groupeSiSet: true));
}
