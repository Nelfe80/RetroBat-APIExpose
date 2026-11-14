using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Lot 4 bis C-6 (14/11) : <c>scores/recovery-status</c>, 2 547 lectures par jour, 418 echecs espaces de 190 s sans recul
/// dans le journal d'une borne. Une lecture par heure au calme, chaque tour pendant une reconstruction, recul apres echec.
/// </summary>
public sealed class RecoveryCadenceTests
{
    [Fact]
    public void Au_calme_une_lecture_par_heure()
    {
        Assert.Equal(TimeSpan.FromHours(1), NelfePlayScoringReporter.ProchainRecovery(NelfePlayScoringReporter.IssueDuRecovery.Calme, 0, 0.5));
        Assert.Equal(TimeSpan.FromMinutes(54), NelfePlayScoringReporter.ProchainRecovery(NelfePlayScoringReporter.IssueDuRecovery.Calme, 0, 0));
    }

    [Fact]
    public void Pendant_une_reconstruction_armee_chaque_tour_de_trois_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(180), NelfePlayScoringReporter.ProchainRecovery(NelfePlayScoringReporter.IssueDuRecovery.Armee, 0, 0.5));
    }

    [Fact]
    public void Apres_des_echecs_la_lecture_recule_jusqu_a_une_heure()
    {
        Assert.Equal(TimeSpan.FromMinutes(3), NelfePlayScoringReporter.ProchainRecovery(NelfePlayScoringReporter.IssueDuRecovery.Echec, 1, 0.5));
        Assert.Equal(TimeSpan.FromMinutes(6), NelfePlayScoringReporter.ProchainRecovery(NelfePlayScoringReporter.IssueDuRecovery.Echec, 2, 0.5));
        Assert.Equal(TimeSpan.FromMinutes(24), NelfePlayScoringReporter.ProchainRecovery(NelfePlayScoringReporter.IssueDuRecovery.Echec, 4, 0.5));
        Assert.Equal(TimeSpan.FromHours(1), NelfePlayScoringReporter.ProchainRecovery(NelfePlayScoringReporter.IssueDuRecovery.Echec, 20, 0.5));
    }
}
