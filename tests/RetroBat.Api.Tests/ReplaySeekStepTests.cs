using RetroBat.Api.Replay.Input;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le pas d'une direction MAINTENUE pendant la lecture d'un replay : il grandit avec la duree
/// de l'appui, par paliers. Cinq secondes au depart pour viser juste, quarante-cinq au bout de
/// quelques secondes pour traverser un long run.
/// </summary>
public class ReplaySeekStepTests
{
    [Theory]
    [InlineData(0, 5)]
    [InlineData(300, 5)]       // la premiere repetition, juste apres la fenetre du tap
    [InlineData(1499, 5)]
    [InlineData(1500, 10)]
    [InlineData(3999, 10)]
    [InlineData(4000, 20)]
    [InlineData(7999, 20)]
    [InlineData(8000, 45)]
    [InlineData(60_000, 45)]   // et il ne grandit plus : un pas d'une minute sauterait les moments
    public void Le_pas_grandit_par_paliers_avec_le_maintien(long tenuMs, double attendu)
        => Assert.Equal(attendu, ReplayInputRouterService.PasDeSeek(tenuMs));

    [Fact]
    public void Les_paliers_sont_ordonnes_et_croissants()
    {
        var paliers = ReplayInputRouterService.PaliersDeSeek;
        for (var i = 1; i < paliers.Length; i++)
        {
            Assert.True(paliers[i].ApresMs > paliers[i - 1].ApresMs, "les seuils montent");
            Assert.True(paliers[i].Pas > paliers[i - 1].Pas, "les pas montent");
        }
        Assert.Equal(0, paliers[0].ApresMs);
    }

    [Fact]
    public void Un_maintien_negatif_rend_le_premier_pas()
        => Assert.Equal(5, ReplayInputRouterService.PasDeSeek(-50));
}
