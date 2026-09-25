using System.IO;
using RetroBat.Api.Media;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// Les medias du store canonique arrivent dans la fiche en URL « /api/v1/media/... ». Lue comme
/// un chemin, l'URL ne resolvait jamais : chaque visite d'un jeu du Data Pack (toute la
/// collection World Scoring) declarait ses medias absents, les « retrouvait », et repoussait la
/// fiche a ES avec « Medias locaux appliques ». Constat du 2026-09-26 sur 19xx.
/// </summary>
public class ApiMediaUrlNeedTests
{
    [Fact]
    public void Une_url_du_store_redevient_le_chemin_de_la_gamelist()
    {
        var chemin = MediaNeedEvaluator.ToGamelistMediaPath(
            "fbneo",
            "/api/v1/media/systems/arcade/games/19xx/artwork/screentitle.png");

        Assert.StartsWith("./", chemin);
        var attendu = Path.GetFullPath(Path.Combine(RetroBatPaths.MediaRoot, "systems", "arcade", "games", "19xx", "artwork", "screentitle.png"));
        var resolu = Path.GetFullPath(Path.Combine(RetroBatPaths.RomsRoot, "fbneo", chemin[2..].Replace('/', Path.DirectorySeparatorChar)));
        Assert.Equal(attendu, resolu, ignoreCase: true);
    }

    [Fact]
    public void Une_url_qui_sort_du_store_ne_designe_rien()
    {
        Assert.Equal("", MediaNeedEvaluator.ToGamelistMediaPath("fbneo", "/api/v1/media/../../appsettings.json"));
    }

    [Theory]
    [InlineData("./images/19xx-image.png")]
    [InlineData("/systems/fbneo/games/3d98067b5792194e0dbe9c59fc8f1b54/media/image")]
    [InlineData("")]
    public void Les_autres_valeurs_passent_telles_quelles(string valeur)
    {
        Assert.Equal(valeur, MediaNeedEvaluator.ToGamelistMediaPath("fbneo", valeur));
    }
    [Fact]
    public void La_boite_2D_se_lit_dans_la_balise_boxart()
    {
        // Vignette reglee sur « boite 2D » : ES et le Data Pack la rangent sous <boxart>.
        var details = new GameDetails();
        details.Extras["boxart"] = "/api/v1/media/systems/arcade/games/19xx/artwork/box/front.png";

        Assert.Equal(
            "/api/v1/media/systems/arcade/games/19xx/artwork/box/front.png",
            MediaNeedEvaluator.ReadSlotValue(details, MediaKinds.BoxFront));
    }

    [Fact]
    public void La_cle_box_2D_garde_la_priorite()
    {
        var details = new GameDetails();
        details.Extras["box-2D"] = "./images/19xx-box.png";
        details.Extras["boxart"] = "./images/autre.png";

        Assert.Equal("./images/19xx-box.png", MediaNeedEvaluator.ReadSlotValue(details, MediaKinds.BoxFront));
    }
}
