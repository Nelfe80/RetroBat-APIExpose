using RetroBat.Api.Controllers;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// APX-LAB-002 (2026-10-10) : le lancement direct coupe RetroAchievements pour une seance par un champ dedie, et
/// emulator, core et system ne peuvent plus ajouter d'arguments au lanceur.
/// </summary>
public class LaunchArgumentsTests
{
    [Theory]
    [InlineData("fbneo", true)]
    [InlineData("mame2003_plus", true)]
    [InlineData("libretro", true)]
    [InlineData("mame64", true)]
    [InlineData("neo-geo.v2", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("fbneo -retroachievements 0", false)]
    [InlineData("-retroachievements", false)]
    [InlineData("fb\"neo", false)]
    [InlineData("fbneo&calc", false)]
    public void Seul_un_nom_passe(string? valeur, bool attendu)
    {
        Assert.Equal(attendu, CommandsController.NomDeLanceur(valeur));
    }

    [Fact]
    public void Un_nom_trop_long_ne_passe_pas()
    {
        Assert.False(CommandsController.NomDeLanceur(new string('a', 65)));
        Assert.True(CommandsController.NomDeLanceur(new string('a', 64)));
    }

    [Fact]
    public void Le_champ_dedie_coupe_retroachievements_pour_la_seance()
    {
        var sans = CommandsController.ArgumentsDuLanceur("-p1index 0", "arcade", "libretro", "fbneo", @"E:\roms\arcade\mslug.zip", false);
        Assert.Equal("-p1index 0 -system arcade -emulator libretro -core fbneo -retroachievements 0 -rom \"E:\\roms\\arcade\\mslug.zip\"", sans);

        var avec = CommandsController.ArgumentsDuLanceur("", "arcade", "libretro", "fbneo", @"E:\roms\arcade\mslug.zip", null);
        Assert.DoesNotContain("-retroachievements", avec);
        Assert.DoesNotContain("-retroachievements", CommandsController.ArgumentsDuLanceur("", "arcade", "libretro", null, "x.zip", true));
    }

    [Fact]
    public void Un_argument_glisse_dans_un_nom_est_refuse()
    {
        Assert.Null(CommandsController.ArgumentsDuLanceur("", "arcade", "libretro", "fbneo -retroachievements 0", "x.zip", null));
        Assert.Null(CommandsController.ArgumentsDuLanceur("", "arcade -rom \"autre.zip\"", "libretro", "fbneo", "x.zip", null));
        Assert.Null(CommandsController.ArgumentsDuLanceur("", "arcade", "-libretro", null, "x.zip", null));
    }

    [Fact]
    public void Un_guillemet_ne_sort_pas_du_chemin_de_la_rom()
    {
        var args = CommandsController.ArgumentsDuLanceur("", "arcade", "libretro", null, "x.zip\" -extra \"y", null);
        Assert.EndsWith("-rom \"x.zip -extra y\"", args);
    }
}
