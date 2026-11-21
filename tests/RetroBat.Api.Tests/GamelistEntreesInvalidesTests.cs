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
              <game><path>./a|b.zip</path><name>Autre</name></game>
            </gameList>
            """);
        Assert.Equal(2, GamelistEntreesInvalides.Retirer(doc.Root));
        var restant = Assert.Single(doc.Root!.Elements("game"));
        Assert.Equal("./ddpdojblka.zip", restant.Element("path")!.Value);
        Assert.Equal(0, GamelistEntreesInvalides.Retirer(doc.Root));
        Assert.Equal(0, GamelistEntreesInvalides.Retirer(null));
    }

    /// <summary>Relecture de la 1.9.210 : le retrait se juge sur le chemin seul, jamais une vraie entree retiree.</summary>
    [Fact]
    public void Le_nettoyage_garde_les_vraies_entrees_meme_singulieres()
    {
        var doc = XDocument.Parse("""
            <gameList>
              <game><path>./ok.zip</path><name>&lt;Jeu&gt;</name></game>
              <game><path>
                ./indente (Europe).zip
              </path><name>Indente</name></game>
              <game><name>Sans chemin</name></game>
              <game><path>./Pok\u00e9mon - \u00e9dition sp\u00e9ciale [!] (Rev 1).gb</path><name>Accents</name></game>
            </gameList>
            """);
        Assert.Equal(0, GamelistEntreesInvalides.Retirer(doc.Root));
        Assert.Equal(4, doc.Root!.Elements("game").Count());
        Assert.True(GamelistEntreesInvalides.CheminDeJeuValide("  ./indente.zip \r\n"));
        Assert.False(GamelistEntreesInvalides.CheminInterdit(null));
        Assert.False(GamelistEntreesInvalides.CheminInterdit("   "));
        Assert.True(GamelistEntreesInvalides.CheminInterdit("./a\tb.zip"));
    }

    /// <summary>Relecture de la 1.9.210 : la fiche vit dans un dossier temporaire, ses medias doivent etre absolus (comme le game.xml d'ES).</summary>
    [Fact]
    public void La_fiche_rend_les_medias_absolus()
    {
        var gamelist = XDocument.Parse("""
            <gameList>
              <game><path>./Dai Ou Jou é.zip</path><name>DDP</name><bezel>./../../plugins/APIExpose/media/b.png</bezel><image>./images/ddp.png</image><thumbnail>E:/x/t.png</thumbnail><desc>./ pas un chemin</desc></game>
            </gameList>
            """);
        var fiche = FicheDeLancement.Construire(@"D:\Mes Jeux\RetroBat\roms\fbneo\Dai Ou Jou é.zip", gamelist);
        var jeu = Assert.Single(fiche.Root!.Elements("game"));
        Assert.Equal("D:/Mes Jeux/RetroBat/roms/fbneo/Dai Ou Jou é.zip", jeu.Element("path")!.Value);
        Assert.Equal("DDP", jeu.Element("name")!.Value);
        Assert.Equal("D:/Mes Jeux/RetroBat/plugins/APIExpose/media/b.png", jeu.Element("bezel")!.Value);
        Assert.Equal("D:/Mes Jeux/RetroBat/roms/fbneo/images/ddp.png", jeu.Element("image")!.Value);
        Assert.Equal("E:/x/t.png", jeu.Element("thumbnail")!.Value);
        Assert.Equal("./ pas un chemin", jeu.Element("desc")!.Value);
    }

    /// <summary>
    /// Relecture de la 1.9.210 : la fiche ecrite se relit comme emulatorLauncher la lit (XmlSerializer sur un FileStream,
    /// EmulatorLauncher.Common GameList.Load), accents et espaces compris, et les arguments la designent entre guillemets.
    /// </summary>
    [Fact]
    public void La_fiche_ecrite_se_relit_accents_et_espaces_compris()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "fiche-test-" + Guid.NewGuid().ToString("N"), "Mes Jeux é", "fbneo");
        Directory.CreateDirectory(dossier);
        try
        {
            var rom = Path.Combine(dossier, "Dai Ou Jou é.zip");
            File.WriteAllText(Path.Combine(dossier, "gamelist.xml"),
                "<?xml version=\"1.0\"?>\n<gameList><game><path>./&lt;Aucune entrée trouvée&gt;</path><name>&lt;Aucune entrée trouvée&gt;</name></game>"
                + "<game><path>./Dai Ou Jou é.zip</path><name>Dai-Ou-Jou é</name></game></gameList>");
            var args = "-system fbneo -emulator libretro -core fbneo -rom \"" + rom + "\"";
            var fiche = FicheDeLancement.Ecrire(args, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            Assert.NotNull(fiche);
            try
            {
                using (var flux = new FileStream(fiche!, FileMode.Open, FileAccess.Read))
                {
                    var lu = (FicheLue?) new System.Xml.Serialization.XmlSerializer(typeof(FicheLue)).Deserialize(flux);
                    var jeu = Assert.Single(lu!.Games);
                    Assert.Equal(rom.Replace('\\', '/'), jeu.Path);
                    Assert.Equal("Dai-Ou-Jou é", jeu.Name);
                }

                Assert.StartsWith("-gameinfo \"" + fiche + "\" -system", FicheDeLancement.AjouterLaFiche(args, fiche!));
            }
            finally
            {
                FicheDeLancement.Effacer(fiche!);
            }

            Assert.False(File.Exists(fiche));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(dossier))!, recursive: true);
        }
    }

    [System.Xml.Serialization.XmlRoot("gameList")]
    public sealed class FicheLue
    {
        [System.Xml.Serialization.XmlElement("game")]
        public List<JeuLu> Games { get; set; } = new();
    }

    public sealed class JeuLu
    {
        [System.Xml.Serialization.XmlElement("path")]
        public string? Path { get; set; }

        [System.Xml.Serialization.XmlElement("name")]
        public string? Name { get; set; }
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
