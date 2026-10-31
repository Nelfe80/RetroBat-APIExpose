using System.Collections.Generic;
using System.Linq;
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
    public void Un_choix_qui_mesure_deja_est_laisse_sauf_si_le_systeme_a_mieux()
    {
        Assert.Null(AImposer(new EmulationStationLaunchConfig("fbneo", "libretro", "fbneo"), coeurChoisi: false, Fbneo));
        // 25/10 : MAME autonome mesure, MAME sous RetroArch mesure ET filme : il passe devant (MAME autonome mis de cote).
        Assert.Equal(("libretro", "mame"), AImposer(new EmulationStationLaunchConfig("mame", "mame64", ""), coeurChoisi: true, Mame));
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

    private static List<EmulationStationSystemEmulatorCore> Libretro(string systeme, params string[] coeurs)
        => coeurs.Select(coeur => new EmulationStationSystemEmulatorCore(systeme, "libretro", coeur)).ToList();

    [Fact]
    public void Un_jeu_de_console_garde_le_coeur_de_sa_console()
    {
        // 31/10 : les listes d'es_systems.cfg, ou FBNeo et MAME sont des secours qui ne lisent pas les ROM de la console.
        var megadrive = Libretro("megadrive", "genesis_plus_gx", "genesis_plus_gx_wide", "picodrive", "fbneo");
        var pcengine = Libretro("pcengine", "mednafen_pce", "mednafen_pce_fast", "fbneo");
        var odyssey2 = Libretro("odyssey2", "o2em", "mame");
        Assert.Equal(("libretro", "genesis_plus_gx"), CoeursObservables.MeilleurLancement(megadrive));
        Assert.Equal(("libretro", "mednafen_pce"), CoeursObservables.MeilleurLancement(pcengine));
        Assert.Equal(("libretro", "o2em"), CoeursObservables.MeilleurLancement(odyssey2));
        Assert.Null(AImposer(new EmulationStationLaunchConfig("megadrive", "libretro", "genesis_plus_gx"), coeurChoisi: false, megadrive));
        // Le choix du joueur qui mesure reste le sien.
        Assert.Null(AImposer(new EmulationStationLaunchConfig("megadrive", "libretro", "picodrive"), coeurChoisi: true, megadrive));
        Assert.Null(AImposer(new EmulationStationLaunchConfig("odyssey2", "libretro", "o2em"), coeurChoisi: true, odyssey2));
    }

    [Fact]
    public void Une_fiche_collee_sous_FBNeo_retrouve_le_coeur_de_sa_console()
    {
        var megadrive = Libretro("megadrive", "genesis_plus_gx", "genesis_plus_gx_wide", "picodrive", "fbneo");
        Assert.Equal(("libretro", "genesis_plus_gx"),
            AImposer(new EmulationStationLaunchConfig("megadrive", "libretro", "fbneo"), coeurChoisi: true, megadrive));
    }

    [Fact]
    public void L_arcade_garde_son_ordre_FBNeo_devant_MAME()
    {
        var cave = Libretro("cave", "fbneo", "fbalpha", "fbalpha2012", "mame", "mame2016", "mame2014");
        Assert.Null(CoeursObservables.CoeurDeConsole(cave));
        Assert.Null(CoeursObservables.CoeurDeConsole(Mame));
        Assert.Equal(("libretro", "fbneo"), AImposer(new EmulationStationLaunchConfig("cave", "libretro", "mame"), coeurChoisi: true, cave));
    }

    [Fact]
    public void Un_coeur_d_arcade_colle_sur_une_console_ne_se_rend_jamais()
    {
        // La fiche en automatique, collee sous FBNeo : le coeur de la console reste en sortant.
        var colle = ConfortARendre(null, "libretro", "fbneo", ("libretro", "genesis_plus_gx"));
        Assert.Equal(Remise.GarderLeNotre, QueRendre(colle, "libretro", "genesis_plus_gx"));
        // Le choix du joueur mis de cote avant la 1.9.94 revient, face au coeur imposé maintenant.
        var mis = new Confort("libretro", "picodrive", "libretro", "fbneo");
        var repris = ConfortARendre(mis, "libretro", "fbneo", ("libretro", "genesis_plus_gx"));
        Assert.Equal(new Confort("libretro", "picodrive", "libretro", "genesis_plus_gx"), repris);
        Assert.Equal(Remise.RemettreConfort, QueRendre(repris, "libretro", "genesis_plus_gx"));
        // En arcade, rien ne change : le choix du joueur revient.
        Assert.Equal(new Confort("libretro", "mame2003_plus", "libretro", "mame"),
            ConfortARendre(null, "libretro", "mame2003_plus", ("libretro", "mame")));
    }

    [Fact]
    public void Un_set_MAME_passe_sous_FBNeo_qui_seul_rend_un_replay()
    {
        // 25/10 : les records sous MAME (RA) n'ont pas de replay ; FBNeo lit les sets MAME des jeux classes.
        var automatique = new EmulationStationLaunchConfig("mame", "libretro", "mame");
        Assert.Equal(("libretro", "fbneo"), FbneoPourUnSetMame(automatique, Mame, fbneoConnaitLeSet: true, refuseIci: false));
        Assert.Equal(("libretro", "fbneo"),
            FbneoPourUnSetMame(new EmulationStationLaunchConfig("mame", "mame64", ""), Mame, fbneoConnaitLeSet: true, refuseIci: false));
        // Un set que FBNeo ne connait pas, ou qu'il a deja refuse ici, reste sous MAME.
        Assert.Null(FbneoPourUnSetMame(automatique, Mame, fbneoConnaitLeSet: false, refuseIci: false));
        Assert.Null(FbneoPourUnSetMame(automatique, Mame, fbneoConnaitLeSet: true, refuseIci: true));
        // Un systeme qui declare FBNeo passe par la regle commune ; hors arcade, rien.
        Assert.Null(FbneoPourUnSetMame(new EmulationStationLaunchConfig("fbneo", "libretro", "fbneo"), Fbneo, true, false));
        Assert.Null(FbneoPourUnSetMame(new EmulationStationLaunchConfig("megadrive", "libretro", "genesis_plus_gx"), Megadrive, true, false));
    }

    [Fact]
    public void Un_coeur_qui_mesure_cede_la_place_a_celui_qui_en_permet_plus()
    {
        var arcade = new List<EmulationStationSystemEmulatorCore>
        {
            new("arcade", "libretro", "mame"), new("arcade", "libretro", "fbneo"),
        };
        Assert.Equal(("libretro", "fbneo"), AImposer(new EmulationStationLaunchConfig("arcade", "libretro", "mame"), true, arcade));
        Assert.Null(AImposer(new EmulationStationLaunchConfig("arcade", "libretro", "fbneo"), true, arcade));
    }

    [Fact]
    public void Le_refus_de_FBNeo_se_lit_dans_le_journal_du_lancement()
    {
        string[] refuse =
        [
            "[INFO] [Core] Loading dynamic libretro core from: \"E:\\RetroBat\\emulators\\retroarch\\core_proxy\\fbneo_libretro.dll\".",
            "[libretro INFO] [FBNeo] Searching all possible locations for romset altbeast",
            "[ERROR] [Content] Failed to load content.",
        ];
        Assert.Equal("altbeast.zip", SetRefuseParFbneo(refuse));
        Assert.Null(SetRefuseParFbneo(refuse.Take(2)));
        string[] mame = ["[INFO] [Core] Loading dynamic libretro core from: \"E:\\x\\mame_libretro.dll\".", "[ERROR] [Content] Failed to load content."];
        Assert.Null(SetRefuseParFbneo(mame));
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
