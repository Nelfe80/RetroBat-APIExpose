using RetroBat.Api.Netplay;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// (25/11) Blazing Star de roms\neogeo lance par Defier partait sous MAME (systeme neogeo : MAME par defaut, FBNeo declare) :
/// partie non certifiable. Un set que FBNeo connait, resolu sous un coeur MAME, part sous libretro/fbneo.
/// </summary>
public sealed class FbneoPourUnJeuQueFbneoConnaitTests
{
    private static EsLaunchArguments.Resolution R(string s, string e, string c) => new(s, e, c);

    [Fact]
    public void Blazing_Star_de_neogeo_resolu_sous_mame_part_sous_fbneo_dans_neogeo()
    {
        var r = NetplayHostService.FbneoPourUnJeuQueFbneoConnait(R("mame", "libretro", "mame"), true, "neogeo", true);
        Assert.Equal(R("neogeo", "libretro", "fbneo"), r);
    }

    [Fact]
    public void Un_set_de_roms_mame_garde_son_systeme_et_passe_sous_fbneo()
    {
        var r = NetplayHostService.FbneoPourUnJeuQueFbneoConnait(R("mame", "libretro", "mame"), true, "mame", false);
        Assert.Equal(R("mame", "libretro", "fbneo"), r);
    }

    [Fact]
    public void Un_joueur_deja_sous_fbneo_n_est_pas_touche()
    {
        var avant = R("neogeo", "libretro", "fbneo");
        Assert.Same(avant, NetplayHostService.FbneoPourUnJeuQueFbneoConnait(avant, true, "neogeo", true));
    }

    [Fact]
    public void Un_set_inconnu_de_fbneo_reste_sous_mame()
    {
        var avant = R("mame", "libretro", "mame");
        Assert.Same(avant, NetplayHostService.FbneoPourUnJeuQueFbneoConnait(avant, false, "mame", false));
    }

    [Fact]
    public void Un_coeur_de_console_n_est_pas_touche()
    {
        var avant = R("megadrive", "libretro", "genesis_plus_gx");
        Assert.Same(avant, NetplayHostService.FbneoPourUnJeuQueFbneoConnait(avant, true, "megadrive", false));
    }
}
