using RetroBat.Api.Netplay;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// (25/11) Blazing Star de roms\neogeo lance par Defier partait sous MAME (le lancement par ES de roms\mame\blazstar.zip,
/// repris par son seul nom de fichier ; le systeme neogeo a FBNeo pour premier coeur) : partie non certifiable. Un set que FBNeo connait, resolu sous un coeur MAME, part sous libretro/fbneo.
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
    public void Blazing_Star_de_neogeo_resolu_sous_mame_autonome_part_sous_fbneo_dans_neogeo()
    {
        // MAME autonome (systeme neogeo : emulateur mame64, coeur neogeo) : c'est MAME aussi.
        var r = NetplayHostService.FbneoPourUnJeuQueFbneoConnait(R("neogeo", "mame64", "neogeo"), true, "neogeo", true);
        Assert.Equal(R("neogeo", "libretro", "fbneo"), r);
        Assert.Equal(R("mame", "libretro", "fbneo"),
            NetplayHostService.FbneoPourUnJeuQueFbneoConnait(R("mame", "groovymame", ""), true, "mame", false));
    }

    [Fact]
    public void Un_lancement_sous_fbneo_repris_d_un_autre_dossier_retrouve_le_systeme_du_sien()
    {
        // Journal du lanceur de la borne : roms\mame\blazstar.zip lance sous « -system mame -core fbneo » (20/11), repris par
        // son seul nom de fichier pour roms\neogeo\blazstar.zip : les reglages de BIOS du systeme neogeo doivent s'appliquer.
        var r = NetplayHostService.FbneoPourUnJeuQueFbneoConnait(R("mame", "libretro", "fbneo"), true, "neogeo", true);
        Assert.Equal(R("neogeo", "libretro", "fbneo"), r);
        // Dossier qui ne declare pas FBNeo : rien ne change.
        var mame = R("mame", "libretro", "fbneo");
        Assert.Same(mame, NetplayHostService.FbneoPourUnJeuQueFbneoConnait(mame, true, "mame", false));
    }

    [Fact]
    public void Un_emulateur_autonome_qui_n_est_pas_mame_n_est_pas_touche()
    {
        var avant = R("neogeo", "raine", "");
        Assert.Same(avant, NetplayHostService.FbneoPourUnJeuQueFbneoConnait(avant, true, "neogeo", true));
    }

    [Fact]
    public void Un_coeur_de_console_n_est_pas_touche()
    {
        var avant = R("megadrive", "libretro", "genesis_plus_gx");
        Assert.Same(avant, NetplayHostService.FbneoPourUnJeuQueFbneoConnait(avant, true, "megadrive", false));
    }
}
