using RetroBat.Api.Replay.Controllers;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La regle venue du site avec un lancement de replay (2026-10-10) : un 1LC se fige a la premiere mort.
/// Elle finit dans du JavaScript : seules les formes d'une regle passent.
/// </summary>
public class ReplayWatchRegleTests
{
    [Theory]
    [InlineData("1lc", "1lc")]
    [InlineData("1cc-multi", "1cc-multi")]
    [InlineData("1lc-power-up", "1lc-power-up")]
    [InlineData("1LC", "")]
    [InlineData("1lc\"};alert(1);//", "")]
    [InlineData("1lc 1cc", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Seule_une_regle_passe(string? recue, string attendue)
    {
        Assert.Equal(attendue, ReplayWatchController.SanitizeRegle(recue));
    }

    [Fact]
    public void Une_regle_trop_longue_ne_passe_pas()
    {
        Assert.Equal("", ReplayWatchController.SanitizeRegle(new string('a', 33)));
    }
}
