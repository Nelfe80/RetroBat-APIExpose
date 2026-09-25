using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'annonce « Partie certifiable » identifie un jeu d'arcade par le SET lance, plus par son nom :
/// ddragon et doubledr s'appellent tous deux « Double Dragon » (2026-09-25).
/// </summary>
public class ScoringSetArcadeTests
{
    [Fact]
    public void En_arcade_le_set_lance_est_transmis()
    {
        Assert.Equal("doubledr", NelfePlayScoringReporter.SetArcade("arcade", " doubledr "));
    }

    [Fact]
    public void Une_console_garde_sa_recherche_par_nom()
    {
        Assert.Null(NelfePlayScoringReporter.SetArcade("megadrive", "Sonic the Hedgehog (USA, Europe)"));
    }

    [Fact]
    public void Sans_set_connu_rien_n_est_transmis()
    {
        Assert.Null(NelfePlayScoringReporter.SetArcade("arcade", ""));
        Assert.Null(NelfePlayScoringReporter.SetArcade("arcade", null));
    }
}
