using System.Xml.Linq;
using RetroBat.Api.Media;
using RetroBat.Api.Netplay;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// 21/11 : DEFIER lancait emulatorLauncher sans -gameinfo ; il parcourait la gamelist du systeme et plantait sur le faux jeu
/// « &lt;Aucune entree trouvee&gt; » d'une liste vide d'ES (« Caracteres non conformes dans le chemin d'acces »).
/// </summary>
public sealed class GamelistEntreesInvalidesTests
{
    [Theory]
    [InlineData("./ddpdojblka.zip", true)]
    [InlineData("./sous dossier/jeu (USA).zip", true)]
    [InlineData("E:/RetroBat/roms/fbneo/mslug3.zip", true)]
    [InlineData("./<Aucune entr\uFFFDe trouv\uFFFDe>", false)]
    [InlineData("./<No entries found>", false)]
    [InlineData("./a|b.zip", false)]
    [InlineData("./a\"b.zip", false)]
    [InlineData("./a\tb.zip", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Chemin_de_jeu(string? chemin, bool valide) => Assert.Equal(valide, GamelistEntreesInvalides.CheminDeJeuValide(chemin));

    [Fact]
    public void Le_pseudo_jeu_est_reconnu_par_son_nom()
    {
        Assert.True(GamelistEntreesInvalides.NomDePseudoJeu("<Aucune entree trouvee>"));
        Assert.False(GamelistEntreesInvalides.NomDePseudoJeu("Metal Slug 3"));
        Assert.False(GamelistEntreesInvalides.NomDePseudoJeu(null));
    }

    [Fact]
    public void Le_nettoyage_retire_le_pseudo_jeu_et_garde_les_vrais()
    {
        var doc = XDocument.Parse("""
            <gameList>
              <game><path>./&lt;Aucune entr?e trouv?e&gt;</path><gameid>0990</gameid><name>&lt;Aucune entr?e trouv?e&gt;</name><lang>en</lang><region>wr</region></game>
              <game><path>./ddpdojblka.zip</path><name>DoDonPachi Dai-Ou-Jou</name></game>
              <game><path>./ok.zip</path><name>&lt;Jeu&gt;</name></game>
            </gameList>
            """);
        Assert.Equal(2, GamelistEntreesInvalides.Retirer(doc.Root));
        var restant = Assert.Single(doc.Root!.Elements("game"));
        Assert.Equal("./ddpdojblka.zip", restant.Element("path")!.Value);
        Assert.Equal(0, GamelistEntreesInvalides.Retirer(doc.Root));
        Assert.Equal(0, GamelistEntreesInvalides.Retirer(null));
    }

    [Fact]
    public void La_fiche_reprend_l_entree_du_jeu_avec_son_chemin_absolu()
    {
        var gamelist = XDocument.Parse("""
            <gameList>
              <game><path>./&lt;Aucune&gt;</path><name>&lt;Aucune&gt;</name></game>
              <game><path>./autre.zip</path><name>Autre</name></game>
              <game><path>./ddpdojblka.zip</path><name>DDP DOJ</name><emulator>libretro</emulator><core>fbneo</core></game>
            </gameList>
            """);
        var fiche = FicheDeLancement.Construire(@"D:\RetroBat\roms\fbneo\ddpdojblka.zip", gamelist);
        var jeu = Assert.Single(fiche.Root!.Elements("game"));
        Assert.Equal("D:/RetroBat/roms/fbneo/ddpdojblka.zip", jeu.Element("path")!.Value);
        Assert.Equal("DDP DOJ", jeu.Element("name")!.Value);
        Assert.Equal("fbneo", jeu.Element("core")!.Value);
        Assert.Equal("path", jeu.Elements().First().Name.LocalName);
    }

    [Fact]
    public void La_fiche_est_minimale_sans_entree_ou_sans_gamelist()
    {
        foreach (var gl in new XDocument?[] { null, XDocument.Parse("<gameList><game><path>./a|b</path></game></gameList>") })
        {
            var jeu = Assert.Single(FicheDeLancement.Construire(@"D:\r\fbneo\ddpdojblka.zip", gl).Root!.Elements("game"));
            Assert.Equal("D:/r/fbneo/ddpdojblka.zip", jeu.Element("path")!.Value);
            Assert.Equal("ddpdojblka", jeu.Element("name")!.Value);
        }
    }

    [Fact]
    public void Les_arguments_du_lanceur_recoivent_la_fiche_une_seule_fois()
    {
        const string args = @"-p1index 0 -system fbneo -emulator libretro -core fbneo -rom ""D:\RetroBat\roms\fbneo\ddpdojblka.zip""";
        Assert.Equal(@"D:\RetroBat\roms\fbneo\ddpdojblka.zip", FicheDeLancement.RomDesArguments(args));
        Assert.False(FicheDeLancement.ADejaUneFiche(args));
        var avec = FicheDeLancement.AjouterLaFiche(args, @"C:\t\f.xml");
        Assert.StartsWith(@"-gameinfo ""C:\t\f.xml"" -p1index 0", avec);
        Assert.True(FicheDeLancement.ADejaUneFiche(avec));
        Assert.Null(FicheDeLancement.RomDesArguments("-system nes"));
    }
}
