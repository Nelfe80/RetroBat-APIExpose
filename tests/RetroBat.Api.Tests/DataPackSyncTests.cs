using System.Text;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les regles pures du Data Pack. L'empreinte de blob est ce qui decide de telecharger ou
/// non : si elle ne se calcule pas EXACTEMENT comme git, chaque fichier paraitrait different
/// et la borne retelechargerait tout a chaque cycle sans qu'aucune erreur ne le signale.
/// </summary>
public class DataPackSyncTests
{
    // Les vecteurs de git lui-meme : `git hash-object` d'un fichier vide et de « hello\n ».
    [Fact]
    public void Le_blob_vide_a_l_empreinte_de_git()
        => Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", DataPackPaths.GitBlobSha1(Array.Empty<byte>()));

    [Fact]
    public void Un_blob_texte_a_l_empreinte_de_git()
        => Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", DataPackPaths.GitBlobSha1(Encoding.ASCII.GetBytes("hello\n")));

    [Fact]
    public void L_empreinte_depend_des_octets_exacts()
    {
        // CRLF et LF ne sont pas le meme blob : c'est pour ca que le depot est en `* -text`.
        Assert.NotEqual(
            DataPackPaths.GitBlobSha1(Encoding.ASCII.GetBytes("a\r\n")),
            DataPackPaths.GitBlobSha1(Encoding.ASCII.GetBytes("a\n")));
    }

    private static readonly HashSet<string> Dossiers = new(new[] { "ram", "dynpanels", "gamelist" }, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("ram/megadrive/sonic.MEM", true)]
    [InlineData("dynpanels/games/19xx.json", true)]
    [InlineData("gamelist/localized/fr/snes.json", true)]
    [InlineData("README.md", false)]                 // a la racine : pas un fichier de resources
    [InlineData("tools/publish.ps1", false)]         // dossier non autorise
    [InlineData("ram/../RetroBat.Api.exe", false)]   // remontee
    [InlineData("ram/./x.MEM", false)]
    [InlineData("/ram/x.MEM", false)]
    [InlineData("ram\\x.MEM", false)]                // git n'ecrit jamais d'antislash
    [InlineData("ram//x.MEM", false)]
    [InlineData("ram/x:y.MEM", false)]               // caractere interdit sous Windows
    [InlineData("", false)]
    public void Seuls_les_chemins_sous_un_dossier_autorise_passent(string chemin, bool attendu)
        => Assert.Equal(attendu, DataPackPaths.Autorise(chemin, Dossiers));

    // Les entrees du zip des cartes : elles vont sous media/, il ne faut pas qu'elles en sortent.
    [Theory]
    [InlineData("1943/artwork/ic/ic-2.png", true)]
    [InlineData("1943/artwork/ic/ic-2.json", true)]
    [InlineData("../RetroBat.Api.exe", false)]
    [InlineData("1943/../../x.png", false)]
    [InlineData("/1943/ic.png", false)]
    [InlineData("C:/x.png", false)]
    [InlineData("", false)]
    public void Une_entree_du_pack_de_cartes_reste_sous_sa_racine(string rel, bool attendu)
        => Assert.Equal(attendu, DataPackPaths.CheminRelatifSur(rel));
}
