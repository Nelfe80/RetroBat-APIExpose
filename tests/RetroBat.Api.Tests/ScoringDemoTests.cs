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
    public void Les_autres_etats_ne_changent_rien()
    {
        // GAME_OVER, CONTINUE_SCREEN, TITLE_SCREEN : ni l'ouverture ni la fermeture d'une démo.
        Assert.True(NelfePlayScoringReporter.EtatDemoApres(true, "GAME_OVER"));
        Assert.True(NelfePlayScoringReporter.EtatDemoApres(true, "CONTINUE_SCREEN"));
        Assert.False(NelfePlayScoringReporter.EtatDemoApres(false, "TITLE_SCREEN"));
        Assert.False(NelfePlayScoringReporter.EtatDemoApres(false, "SCORE_STATE"));
    }
}
