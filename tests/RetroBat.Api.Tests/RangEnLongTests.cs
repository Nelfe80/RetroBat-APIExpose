using System.Text.Json;
using RetroBat.Api.Controllers;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// API-4 (14/11) : score et rang du site lus en long. Au-dela de 2 147 483 647, GetInt32 levait et records/me comme
/// records/leaderboard repondaient « pas de rang ».
/// </summary>
public class RangEnLongTests
{
    private static JsonElement Json(string texte) => JsonDocument.Parse(texte).RootElement.Clone();

    [Fact]
    public void Un_score_au_dela_d_un_int_est_lu()
    {
        var racine = Json("""{"score": 4123456789, "rank": 2, "of": 31}""");
        Assert.Equal(4123456789L, NelfePlayController.NombreEntier(racine, "score"));
        Assert.Equal(2L, NelfePlayController.NombreEntier(racine, "rank"));
        Assert.Equal(31L, NelfePlayController.NombreEntier(racine, "of"));
    }

    [Fact]
    public void Un_nombre_ecrit_avec_exposant_reste_entier()
        => Assert.Equal(15_000_000_000L, NelfePlayController.NombreEntier(Json("""{"score": 1.5e10}"""), "score"));

    [Theory]
    [InlineData("""{"score": "12"}""")]
    [InlineData("""{"score": null}""")]
    [InlineData("""{"score": 1.25}""")]
    [InlineData("""{"autre": 1}""")]
    [InlineData("""[1]""")]
    public void Rien_d_entier_donne_null_sans_exception(string texte)
        => Assert.Null(NelfePlayController.NombreEntier(Json(texte), "score"));
}
