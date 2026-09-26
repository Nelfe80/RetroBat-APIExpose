using System.IO;
using System.Linq;
using System.Xml.Linq;
using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// Rapport testeur du 2026-09-26 : un jeu ajoute a la borne manquait a World Scoring. Avec
/// ParseGamelistOnly=true, ES ne lit plus les dossiers de ROMs ; notre collection listait le jeu,
/// ES ne le connaissait pas. La collection inscrit desormais ces jeux dans leur gamelist.
/// </summary>
public class CollectionJeuxInconnusDEsTests
{
    private static readonly string Racine = Path.Combine(Path.GetTempPath(), "roms", "fbneo");

    [Fact]
    public void Un_jeu_absent_de_la_gamelist_y_est_inscrit_avec_son_nom()
    {
        var gamelist = new XElement("gameList");

        var ajoutes = GamelistUpdateService.AjouterEntreesAbsentes(gamelist, Racine,
            new[] { (Path.Combine(Racine, "19xx.zip"), "19XX: The War Against Destiny") });

        Assert.Equal(1, ajoutes);
        var jeu = Assert.Single(gamelist.Elements("game"));
        Assert.Equal("./19xx.zip", jeu.Element("path")!.Value);
        Assert.Equal("19XX: The War Against Destiny", jeu.Element("name")!.Value);
    }

    [Fact]
    public void Un_jeu_deja_liste_n_est_pas_double()
    {
        var gamelist = XElement.Parse("<gameList><game><path>./19XX.zip</path><name>19XX</name></game></gameList>");

        var ajoutes = GamelistUpdateService.AjouterEntreesAbsentes(gamelist, Racine,
            new[] { (Path.Combine(Racine, "19xx.zip"), "autre nom") });

        Assert.Equal(0, ajoutes);
        Assert.Single(gamelist.Elements("game"));
    }

    [Fact]
    public void Sans_nom_connu_le_fichier_nomme_le_jeu()
    {
        var gamelist = new XElement("gameList");

        GamelistUpdateService.AjouterEntreesAbsentes(gamelist, Racine, new[] { (Path.Combine(Racine, "1942.zip"), "") });

        Assert.Equal("1942", gamelist.Elements("game").Single().Element("name")!.Value);
    }

    [Fact]
    public void Rien_n_est_inscrit_hors_du_dossier_du_systeme()
    {
        var gamelist = new XElement("gameList");
        var ailleurs = Path.Combine(Path.GetTempPath(), "roms", "mame", "19xx.zip");

        Assert.Equal(0, GamelistUpdateService.AjouterEntreesAbsentes(gamelist, Racine, new[] { (ailleurs, "19XX") }));
        Assert.Empty(gamelist.Elements("game"));
    }
}
