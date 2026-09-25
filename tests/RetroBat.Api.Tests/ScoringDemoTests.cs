using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Metal Slug 3 sous MAME, 2026-09-25 : l'attract a joué seul jusqu'à 17 700, puis le joueur a fait
/// 1 700. Le découpage a retenu 17 700 : le score de la DÉMO serait parti au classement sous son nom.
/// Deux défauts s'y cumulaient : les états du pont Lua n'atteignaient pas la détection de la démo,
/// et beaucoup de .MEM ne déclarent aucun état « en jeu », si bien qu'une démo vue une fois ne se
/// refermait jamais. La démo s'ouvre sur DEMO_MODE et se referme sur GAME_PLAYING ou sur START.
/// </summary>
public class ScoringDemoTests
{
    [Fact]
    public void La_demo_s_ouvre_sur_DEMO_MODE()
    {
        Assert.True(NelfePlayScoringReporter.EtatDemoApres(false, "DEMO_MODE"));
    }

    [Fact]
    public void Un_etat_en_jeu_referme_la_demo()
    {
        Assert.False(NelfePlayScoringReporter.EtatDemoApres(true, "GAME_PLAYING"));
    }

    [Fact]
    public void Pendant_une_partie_ouverte_par_START_un_DEMO_MODE_est_ignore()
    {
        // Mesuré en direct : « Soft Dip - toggle demo sound », étiqueté DEMO_MODE, s'allumait en
        // plein jeu, et la partie du joueur (1 900) passait pour de la démo.
        var (demo, enJeu) = NelfePlayScoringReporter.EtatsApres(demo: false, enJeu: true, "DEMO_MODE");
        Assert.False(demo);
        Assert.True(enJeu);
    }

    [Fact]
    public void Hors_partie_un_DEMO_MODE_ouvre_bien_la_demo()
    {
        var (demo, _) = NelfePlayScoringReporter.EtatsApres(demo: false, enJeu: false, "DEMO_MODE");
        Assert.True(demo);
    }

    [Fact]
    public void Le_GAME_OVER_ferme_la_partie()
    {
        var (_, enJeu) = NelfePlayScoringReporter.EtatsApres(demo: false, enJeu: true, "GAME_OVER");
        Assert.False(enJeu);
    }

    [Fact]
    public void Seules_les_lectures_en_jeu_restent_le_scenario_mesure()
    {
        // Metal Slug 3, 13:23-13:25 le 25/09 : la démo monte à 6 010, START, le joueur fait 2 600,
        // game over, la démo reprend et remonte à 9 000. Seul le 2 600 est à lui.
        var traj = new List<(long frame, long total)>
        {
            (0, 100), (0, 2900), (0, 6010),          // démo, avant tout START
            (0, 100), (0, 1200), (0, 2600),          // la partie du joueur
            (0, 300), (0, 9000),                     // la démo qui reprend après le game over
        };
        var horsJeu = new List<bool> { true, true, true, false, false, false, true, true };

        var enJeu = NelfePlayScoringReporter.FiltrerEnJeu(traj, horsJeu);
        var meilleur = NelfePlayScoringReporter.SelectBestRun(enJeu);

        Assert.Equal(3, enJeu.Count);
        Assert.Equal(2600, meilleur[^1].total);   // et pas 9 000, ni 6 010
    }

    [Fact]
    public void Les_autres_etats_ne_changent_rien()
    {
        // GAME_OVER, CONTINUE_SCREEN, TITLE_SCREEN : ni l'ouverture ni la fermeture d'une démo.
        Assert.True(NelfePlayScoringReporter.EtatDemoApres(true, "GAME_OVER"));
        Assert.True(NelfePlayScoringReporter.EtatDemoApres(true, "CONTINUE_SCREEN"));
        Assert.False(NelfePlayScoringReporter.EtatDemoApres(false, "TITLE_SCREEN"));
        Assert.False(NelfePlayScoringReporter.EtatDemoApres(false, "SCORE_STATE"));
    }
}
