using RetroBat.Api.Replay.Recording;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// RetroArch 1.22.2 plante à l'ouverture d'un enregistrement sous le cœur libretro MAME (encodeur de
/// points de contrôle, dernier bloc lu sur 16 Ko entiers). Prouvé le 25 septembre 2026 par sept
/// vidages et par une nocturne qui, elle, a tenu. D'où une porte : sous ce cœur, on n'envoie pas
/// RECORD_REPLAY.
///
/// Le piège que ces tests gèlent : tous les cœurs MAME partagent le systemid « mame ». La porte doit
/// viser MAME libretro SEUL, jamais MAME 2003-Plus ni les autres, qui enregistrent aujourd'hui.
/// </summary>
public class ReplayPorteCoeurTests
{
    [Theory]
    [InlineData(@"E:\RetroBat\saves\mame\libretro.mame", "mame")]
    [InlineData(@"E:\RetroBat\saves\mame\libretro.mame\", "mame")]
    [InlineData(@"E:\RetroBat\saves\mame\libretro.mame2003_plus", "mame2003_plus")]
    [InlineData(@"E:\RetroBat\saves\fbneo\libretro.fbneo", "fbneo")]
    [InlineData("E:/RetroBat/saves/megadrive/libretro.genesis_plus_gx", "genesis_plus_gx")]
    public void Le_coeur_se_lit_dans_le_dossier_que_pose_RetroBat(string dossier, string attendu)
    {
        Assert.Equal(attendu, ReplayRecorderService.CoeurDuDossier(dossier));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"E:\RetroBat\saves\mame")]
    [InlineData(@"C:\Autre\dossier\sans\forme")]
    public void Un_dossier_sans_la_forme_ne_nomme_aucun_coeur(string? dossier)
    {
        Assert.Null(ReplayRecorderService.CoeurDuDossier(dossier));
    }

    [Fact]
    public void MAME_libretro_est_bloque_par_le_reglage_par_defaut()
    {
        Assert.True(ReplayRecorderService.EstSansEnregistrement("mame", "mame_libretro"));
        Assert.True(ReplayRecorderService.EstSansEnregistrement("mame", "libretro.mame"));
        Assert.True(ReplayRecorderService.EstSansEnregistrement("mame", " MAME_LIBRETRO.dll "));
    }

    [Fact]
    public void Les_autres_coeurs_MAME_continuent_d_enregistrer()
    {
        // LE PIÈGE : même systemid « mame » partout. Seul le nom du dossier les sépare.
        Assert.False(ReplayRecorderService.EstSansEnregistrement("mame2003_plus", "mame_libretro"));
        Assert.False(ReplayRecorderService.EstSansEnregistrement("mame2010", "mame_libretro"));
        Assert.False(ReplayRecorderService.EstSansEnregistrement("mame2016", "mame_libretro"));
        Assert.False(ReplayRecorderService.EstSansEnregistrement("fbneo", "mame_libretro"));
    }

    [Fact]
    public void Un_reglage_vide_leve_la_porte()
    {
        // Le jour où RetroBat livre un RetroArch corrigé : plus rien n'est bloqué.
        Assert.False(ReplayRecorderService.EstSansEnregistrement("mame", ""));
        Assert.False(ReplayRecorderService.EstSansEnregistrement("mame", "   "));
        Assert.False(ReplayRecorderService.EstSansEnregistrement("mame", null));
    }

    [Fact]
    public void Un_coeur_inconnu_ne_bloque_rien()
    {
        // RetroArch n'a pas répondu, ou le dossier ne suit pas la forme : la porte ne vise qu'un
        // plantage identifié, elle ne s'étend pas au doute.
        Assert.False(ReplayRecorderService.EstSansEnregistrement(null, "mame_libretro"));
    }

    [Fact]
    public void La_liste_accepte_plusieurs_coeurs()
    {
        const string liste = "mame_libretro, mame2016_libretro";
        Assert.True(ReplayRecorderService.EstSansEnregistrement("mame", liste));
        Assert.True(ReplayRecorderService.EstSansEnregistrement("mame2016", liste));
        Assert.False(ReplayRecorderService.EstSansEnregistrement("mame2003_plus", liste));
    }
}
