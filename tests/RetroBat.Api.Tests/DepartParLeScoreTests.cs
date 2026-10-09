using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le depart du replay au score qui monte (player, 2026-10-09) : l'API avait perdu sa manette a la
/// sortie de RetroArch. Les appuis lus avant faisaient croire la borne lue, et une partie entiere
/// d'Alex Kidd est partie sans replay. Un changement de manettes remet le filet en place.
/// </summary>
public class DepartParLeScoreTests
{
    [Fact]
    public void Sur_une_borne_lue_le_score_ne_lance_rien()
    {
        Assert.False(NelfePlayScoringReporter.DepartParLeScore(attenteMontee: true, panelLu: true, departsParScore: 0));
    }

    [Fact]
    public void Manettes_changees_et_aucun_appui_lu_depuis_le_score_lance_le_replay()
    {
        Assert.True(NelfePlayScoringReporter.DepartParLeScore(attenteMontee: true, panelLu: false, departsParScore: 0));
    }

    [Fact]
    public void Trois_departs_par_partie_au_plus()
    {
        Assert.True(NelfePlayScoringReporter.DepartParLeScore(attenteMontee: true, panelLu: false, departsParScore: 2));
        Assert.False(NelfePlayScoringReporter.DepartParLeScore(attenteMontee: true, panelLu: false, departsParScore: 3));
    }

    [Fact]
    public void Un_enregistrement_parti_n_attend_plus_de_montee()
    {
        Assert.False(NelfePlayScoringReporter.DepartParLeScore(attenteMontee: false, panelLu: false, departsParScore: 0));
    }
}
