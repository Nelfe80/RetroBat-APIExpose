using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Les bornes de la performance dans son replay (CDC video V2, §4.4).</summary>
public sealed class BornesVideoTests
{
    private static List<(long frame, long total)> Points(params (long frame, long total)[] p) => p.ToList();

    [Fact]
    public void Un_run_seul_part_du_debut_de_l_enregistrement_et_finit_trois_secondes_apres_son_dernier_score()
    {
        var lectures = Points((1100, 100), (1500, 300), (2000, 900));
        var bornes = BornesVideo.Calculer(1000, 5000, lectures, lectures, []);
        Assert.Equal((0L, 2000 - 1000 + BornesVideo.MargeDeFin), bornes);
    }

    [Fact]
    public void La_fin_tombe_sur_la_coupure_qui_ferme_le_run_plus_la_marge()
    {
        var lectures = Points((1100, 100), (2000, 900));
        var bornes = BornesVideo.Calculer(1000, 9000, lectures, lectures, [2600]);
        Assert.Equal((0L, 2600 - 1000 + BornesVideo.MargeDeFin), bornes);
    }

    [Fact]
    public void Une_coupure_avant_le_dernier_score_n_avance_pas_la_fin()
    {
        var lectures = Points((1100, 100), (2000, 900));
        var bornes = BornesVideo.Calculer(1000, 9000, lectures, lectures, [1500]);
        Assert.Equal((0L, 2000 - 1000 + BornesVideo.MargeDeFin), bornes);
    }

    [Fact]
    public void La_fin_ne_depasse_pas_la_fin_de_l_enregistrement()
    {
        var lectures = Points((1100, 100), (2000, 900));
        var bornes = BornesVideo.Calculer(1000, 2050, lectures, lectures, []);
        Assert.Equal((0L, 1050L), bornes);
    }

    [Fact]
    public void Un_run_precede_d_un_autre_dans_l_enregistrement_part_de_la_coupure_qui_les_separe()
    {
        var lectures = Points((1100, 100), (1400, 500), (2100, 0), (2500, 800), (3000, 1600));
        var run = Points((2100, 0), (2500, 800), (3000, 1600));
        var bornes = BornesVideo.Calculer(1000, 9000, lectures, run, [1800]);
        Assert.Equal((800L, 3000 - 1000 + BornesVideo.MargeDeFin), bornes);
    }

    [Fact]
    public void Sans_coupure_connue_le_run_suivant_part_de_la_derniere_lecture_du_precedent()
    {
        var lectures = Points((1100, 100), (1400, 500), (2500, 800), (3000, 1600));
        var run = Points((2500, 800), (3000, 1600));
        var bornes = BornesVideo.Calculer(1000, 9000, lectures, run, []);
        Assert.Equal((400L, 3000 - 1000 + BornesVideo.MargeDeFin), bornes);
    }

    [Fact]
    public void Le_1LC_s_arrete_a_la_mort_plus_la_marge()
    {
        var lectures = Points((1100, 100), (1500, 300), (2000, 900), (3000, 1500));
        var run1lc = Points((1100, 100), (1500, 300));
        var bornes = BornesVideo.Calculer(1000, 9000, lectures, run1lc, [1700]);
        Assert.Equal((0L, 1700 - 1000 + BornesVideo.MargeDeFin), bornes);
    }

    [Fact]
    public void Un_run_hors_de_l_enregistrement_n_a_pas_de_bornes()
    {
        var lectures = Points((100, 10), (200, 20));
        Assert.Null(BornesVideo.Calculer(1000, 9000, lectures, lectures, []));
        Assert.Null(BornesVideo.Calculer(1000, 9000, Points(), Points(), []));
    }
}
