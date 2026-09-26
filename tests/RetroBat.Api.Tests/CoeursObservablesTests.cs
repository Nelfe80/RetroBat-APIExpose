using System.Collections.Generic;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Liste blanche des coeurs qui mesurent (demande user 2026-09-26) : 19xx etait propose sous le
/// systeme mame en automatique, RetroBat a pris MAME 2003-Plus, et aucun score n'a pu partir.
/// </summary>
public class CoeursObservablesTests
{
    private static readonly List<EmulationStationSystemEmulatorCore> SystemeMame = new()
    {
        new("mame", "libretro", "mame"), new("mame", "libretro", "mame2016"), new("mame", "libretro", "mame2003_plus"),
        new("mame", "mame64", ""), new("mame", "groovymame", ""),
    };

    private static readonly List<EmulationStationSystemEmulatorCore> SystemeFbneo = new()
    {
        new("fbneo", "libretro", "fbneo"), new("fbneo", "libretro", "fbalpha2012"), new("fbneo", "fbneo", "arcade"),
    };

    private static CoeursObservables.Verdict Juger(string emulateur, string coeur, bool choisi, List<EmulationStationSystemEmulatorCore> systeme)
        => CoeursObservables.Juger(new EmulationStationLaunchConfig("x", emulateur, coeur), choisi, systeme);

    [Theory]
    [InlineData("libretro", "mame", true)]
    [InlineData("libretro", "fbneo", true)]
    [InlineData("mame64", "", true)]
    [InlineData("libretro", "mame2003_plus", false)]
    [InlineData("libretro", "mame2016", false)]
    [InlineData("libretro", "fbalpha2012", false)]
    [InlineData("groovymame", "", true)]
    [InlineData("fbneo", "arcade", false)]
    public void Un_coeur_designe_est_juge_sur_la_liste_blanche(string emulateur, string coeur, bool attendu)
        => Assert.Equal(attendu, Juger(emulateur, coeur, choisi: true, SystemeMame).Observable);

    [Fact]
    public void Le_systeme_mame_en_automatique_est_imprevisible()
    {
        // Premier coeur declare : mame. Mais RetroBat choisit jeu par jeu (FindBestMameCore).
        var verdict = Juger("libretro", "mame", choisi: false, SystemeMame);

        Assert.False(verdict.Observable);
        Assert.Contains("automatique", verdict.Raison);
    }

    [Fact]
    public void Le_systeme_fbneo_en_automatique_part_sur_fbneo()
        => Assert.True(Juger("libretro", "fbneo", choisi: false, SystemeFbneo).Observable);

    [Fact]
    public void Hors_arcade_on_ne_juge_pas()
        => Assert.True(Juger("libretro", "genesis_plus_gx", choisi: false, new List<EmulationStationSystemEmulatorCore>
        {
            new("megadrive", "libretro", "genesis_plus_gx"), new("megadrive", "libretro", "picodrive"),
        }).Observable);

    [Fact]
    public void Le_choix_du_jeu_passe_devant_le_systeme()
    {
        // Le choix fait pour 19xx dans ES (fiche du jeu), le systeme restant en automatique.
        var (fusion, coeurChoisi) = EmulationStationSystemConfigService.ReglagesDuJeu(
            "mame", "libretro", "mame", new Dictionary<string, string>());

        Assert.True(coeurChoisi);
        Assert.Equal("mame", fusion["mame.core"]);
    }

    [Fact]
    public void Un_auto_du_jeu_laisse_le_systeme_en_automatique()
    {
        var (_, coeurChoisi) = EmulationStationSystemConfigService.ReglagesDuJeu(
            "mame", "auto", "auto", new Dictionary<string, string>());

        Assert.False(coeurChoisi);
    }
}
