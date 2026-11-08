using RetroBat.Api.Controllers;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// 09/11 : le CHALLENGE avec le Deck passait a FBNeo le chemin d'ES (« E:/RetroBat/roms/fbneo/mslug3.zip ») ; FBNeo cherche
/// le dossier du jeu apres le dernier « \ » et affichait « romset missing files ». La ligne du lanceur porte des « \ ».
/// </summary>
public sealed class CheminWindowsTests
{
    [Fact]
    public void Le_chemin_d_ES_part_avec_des_separateurs_Windows()
    {
        var ligne = CommandsController.ArgumentsDuLanceur("", "fbneo", "libretro", "fbneo", "E:/RetroBat/roms/fbneo/mslug3.zip", null);
        Assert.NotNull(ligne);
        Assert.EndsWith("-rom \"" + @"E:\RetroBat\roms\fbneo\mslug3.zip" + "\"", ligne);
    }

    [Fact]
    public void Un_chemin_deja_Windows_ne_change_pas()
        => Assert.Equal(@"E:\RetroBat\roms\fbneo\bgaregga.zip", CommandsController.CheminWindows(@"E:\RetroBat\roms\fbneo\bgaregga.zip"));
}
