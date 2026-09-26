using System.Collections.Generic;
using RetroBat.Api.Infrastructure;
using Xunit;
using static RetroBat.Api.Infrastructure.WorldScoringLancementService;

namespace RetroBat.Api.Tests;

/// <summary>
/// Regle user 2026-09-27 : dans World Scoring, notre coeur fonctionnel s'impose quel que soit le
/// choix de confort ; dans le systeme du jeu, le choix du joueur est conserve.
/// </summary>
public class WorldScoringLancementTests
{
    private static readonly List<EmulationStationSystemEmulatorCore> Mame = new()
    {
        new("mame", "libretro", "mame"), new("mame", "libretro", "mame2003_plus"), new("mame", "mame64", ""),
    };

    private static readonly List<EmulationStationSystemEmulatorCore> Fbneo = new()
    {
        new("fbneo", "libretro", "fbneo"), new("fbneo", "libretro", "fbalpha2012"),
    };

    private static readonly List<EmulationStationSystemEmulatorCore> Megadrive = new()
    {
        new("megadrive", "libretro", "genesis_plus_gx"), new("megadrive", "kega-fusion", "auto"),
    };

    [Fact]
    public void Un_choix_de_confort_qui_ne_mesure_pas_recoit_notre_coeur()
        => Assert.Equal(("libretro", "mame"),
            AImposer(new EmulationStationLaunchConfig("mame", "libretro", "mame2003_plus"), coeurChoisi: true, Mame));

    [Fact]
    public void Le_systeme_mame_en_automatique_recoit_notre_coeur()
        => Assert.Equal(("libretro", "mame"),
            AImposer(new EmulationStationLaunchConfig("mame", "libretro", "mame"), coeurChoisi: false, Mame));

    [Fact]
    public void Un_choix_qui_mesure_deja_est_laisse()
    {
        Assert.Null(AImposer(new EmulationStationLaunchConfig("mame", "mame64", ""), coeurChoisi: true, Mame));
        Assert.Null(AImposer(new EmulationStationLaunchConfig("fbneo", "libretro", "fbneo"), coeurChoisi: false, Fbneo));
    }

    [Fact]
    public void FBNeo_passe_devant_MAME_et_hors_arcade_le_premier_coeur_libretro()
    {
        Assert.Equal(("libretro", "fbneo"), CoeursObservables.MeilleurLancement(new List<EmulationStationSystemEmulatorCore>
        {
            new("arcade", "libretro", "mame2003_plus"), new("arcade", "libretro", "mame"), new("arcade", "libretro", "fbneo"),
        }));
        Assert.Equal(("libretro", "genesis_plus_gx"),
            AImposer(new EmulationStationLaunchConfig("megadrive", "kega-fusion", "auto"), coeurChoisi: true, Megadrive));
    }

    [Fact]
    public void En_sortant_le_choix_de_confort_est_rendu()
        => Assert.Equal(Remise.RemettreConfort,
            QueRendre(new Confort("libretro", "mame2003_plus", "libretro", "mame"), "libretro", "mame"));

    [Fact]
    public void Un_jeu_qui_etait_en_automatique_garde_notre_coeur()
        => Assert.Equal(Remise.GarderLeNotre,
            QueRendre(new Confort("", "", "libretro", "mame"), "libretro", "mame"));

    [Fact]
    public void Un_choix_change_entre_temps_par_le_joueur_fait_foi()
        => Assert.Equal(Remise.ChoixDuJoueur,
            QueRendre(new Confort("libretro", "mame2003_plus", "libretro", "mame"), "mame64", ""));
}
