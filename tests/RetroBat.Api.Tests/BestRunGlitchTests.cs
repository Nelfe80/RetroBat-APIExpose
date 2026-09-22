using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le « meilleur run » ne doit jamais etre une lecture parasite. Ms. Pac-Man sous MAME
/// standalone, 2026-09-22 : 430 → 906030 → 440. La valeur qui suit REPREND d'avant le pic,
/// donc le pic n'etait pas un score ; une vraie nouvelle partie, elle, repart de plus bas.
/// </summary>
public sealed class BestRunGlitchTests
{
    private static List<(long frame, long total)> Traj(params long[] totaux)
        => totaux.Select((t, i) => ((long) i * 60, t)).ToList();

    [Fact]
    public void Un_pic_isole_suivi_d_une_reprise_est_ignore()
    {
        var run = NelfePlayScoringReporter.SelectBestRun(Traj(0, 10, 200, 430, 906030, 440, 1250));
        Assert.Equal(1250, run[^1].total);
        Assert.DoesNotContain(run, p => p.total == 906030);
    }

    [Fact]
    public void Deux_lectures_parasites_de_suite_sont_ignorees_aussi()
    {
        var run = NelfePlayScoringReporter.SelectBestRun(Traj(0, 100, 15777936, 15777940, 110, 680));
        Assert.Equal(680, run[^1].total);
        Assert.DoesNotContain(run, p => p.total >= 15777936);
    }

    [Fact]
    public void Une_vraie_nouvelle_partie_reste_un_run_a_part()
    {
        // 2 480 puis on relance : 0, 300. Le meilleur run est bien le premier, 2 480.
        var run = NelfePlayScoringReporter.SelectBestRun(Traj(0, 500, 2480, 0, 300));
        Assert.Equal(2480, run[^1].total);
    }

    [Fact]
    public void Un_pic_final_sans_lecture_apres_ne_peut_pas_etre_tranche()
    {
        // Rien ne suit le pic : on ne peut pas savoir, la regle ne s'applique pas.
        var run = NelfePlayScoringReporter.SelectBestRun(Traj(0, 500, 906030));
        Assert.Equal(906030, run[^1].total);
    }
}
