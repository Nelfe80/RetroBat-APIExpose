using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le bandeau « Partie certifiable » : un par lancement, sans aucune duree (regle user 2026-10-05).
/// Sous MAME, le wrapper puis le pont Lua attestent la meme partie : le second bandeau se tait. Une
/// partie RELANCEE a toujours le sien (Lorenzo : plus de bandeau des la deuxieme partie).
/// </summary>
public class PrevolParLancementTests
{
    [Fact]
    public void Le_second_attesteur_de_la_meme_partie_ne_redit_rien()
    {
        var cle = NelfePlayScoringReporter.CleDuPrevol(7, "arcade", "1942", "Partie certifiable", "pour le classement");
        Assert.True(NelfePlayScoringReporter.PrevolDejaDit(cle, cle));
    }

    [Fact]
    public void Une_partie_relancee_a_son_bandeau_aussitot()
    {
        var premiere = NelfePlayScoringReporter.CleDuPrevol(7, "arcade", "1942", "Partie certifiable", "pour le classement");
        var relance = NelfePlayScoringReporter.CleDuPrevol(8, "arcade", "1942", "Partie certifiable", "pour le classement");
        Assert.False(NelfePlayScoringReporter.PrevolDejaDit(relance, premiere));
    }

    [Fact]
    public void Un_autre_verdict_dans_la_meme_partie_s_affiche()
    {
        var certifiable = NelfePlayScoringReporter.CleDuPrevol(7, "arcade", "1942", "Partie certifiable", "pour le classement");
        var non = NelfePlayScoringReporter.CleDuPrevol(7, "arcade", "1942", "Partie non certifiable", "rewind");
        Assert.False(NelfePlayScoringReporter.PrevolDejaDit(non, certifiable));
    }
}
