using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Metal Slug 3 sous MAME, 2026-09-25 : à chaque lancement, la machine lit 63 au démarrage puis se
/// réinitialise d'elle-même une quinzaine de secondes plus tard. Le pont Lua refermait là une session
/// de 63 points. Un score qui n'est jamais monté n'est pas une partie.
/// </summary>
public class ScoringPersonneNaJoueTests
{
    [Fact]
    public void La_seule_lecture_de_demarrage_n_est_pas_une_partie()
    {
        Assert.False(NelfePlayScoringReporter.ScoreAMonte([(0, 63)]));
    }

    [Fact]
    public void Un_63_qui_retombe_a_zero_n_est_pas_une_partie()
    {
        Assert.False(NelfePlayScoringReporter.ScoreAMonte([(0, 63), (12, 0)]));
    }

    [Fact]
    public void Sans_aucune_lecture_personne_n_a_joue()
    {
        Assert.False(NelfePlayScoringReporter.ScoreAMonte([]));
    }

    [Fact]
    public void Une_vraie_partie_fait_monter_le_score()
    {
        // La partie de 14:11 : 63 au démarrage, remise à zéro, puis le joueur marque.
        Assert.True(NelfePlayScoringReporter.ScoreAMonte([(0, 63), (12, 0), (900, 100), (1400, 900)]));
    }
}
