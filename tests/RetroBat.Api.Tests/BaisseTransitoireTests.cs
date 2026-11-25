using RetroBat.Api.Infrastructure;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LES BAISSES TRANSITOIRES (26/11, 19XX). Trace REELLE de la borne (replay rp_06H16PG8G14H4X85CAZN8QAN6G, boite noire,
/// lectures players.1.score ; images interpolees sur les couples temps/image de la boite noire) : le jeu affiche 183
/// pendant l'ecran de fin de stage 1 (apres 49 900), puis 84 900, puis 183, puis reprend a 85 000. SelectBestRun coupait
/// a ces baisses : 1CC de 104 600 limite a la fin de la partie (run 7033 a 8707 au passeport).
/// Vies perdues du joueur 1 : images 2541 et 5087 ; GAME OVER 9261.
/// </summary>
public class BaisseTransitoireTests
{
    private static readonly List<(long frame, long total)> Reel19xx =
    [
        (1716, 100), (1732, 200), (1922, 1700), (2005, 1800), (2035, 1900), (2162, 2200), (2170, 2500), (2178, 2800),
        (2195, 2900), (2330, 3200), (2334, 3600), (2337, 4000), (2348, 4300), (2356, 4600), (2397, 4700), (2405, 4800),
        (2416, 4900), (2423, 5000), (2425, 5100), (2430, 5200), (2440, 5300), (2443, 5400), (2459, 5500), (2468, 5600),
        (2543, 5800), (2546, 10400), (2610, 10700), (2623, 10800), (2631, 10900), (2639, 11000), (2650, 11300), (2685, 12300),
        (2693, 12400), (2697, 12500), (2699, 12600), (2748, 14100), (2780, 14200), (2785, 14300), (2795, 14600), (2802, 14700),
        (2807, 14800), (2813, 14900), (2829, 15000), (2837, 15100), (2839, 15200), (2845, 15400), (2859, 15500), (2880, 15600),
        (2885, 15700), (2887, 15800), (2899, 15900), (2911, 16000), (2915, 16100), (2947, 16300), (2959, 16400), (2965, 16500),
        (2973, 16600), (2977, 16700), (2978, 16800), (2983, 16900), (2987, 17000), (2992, 17100), (3000, 17200), (3008, 17300),
        (3043, 17500), (3051, 17800), (3061, 17900), (3065, 18000), (3074, 18100), (3087, 18200), (3095, 18300), (3103, 18400),
        (3107, 18500), (3117, 18600), (3127, 18700), (3128, 18800), (3140, 18900), (3187, 19000), (3190, 19300), (3215, 19400),
        (3221, 19500), (3229, 19600), (3251, 19700), (3253, 19800), (3265, 19900), (3268, 20000), (3270, 20100), (3277, 20200),
        (3279, 20300), (3323, 20600), (3324, 20700), (3329, 20800), (3331, 21100), (3336, 21400), (3339, 21700), (3345, 21800),
        (3347, 22100), (3349, 22400), (3351, 22700), (3357, 23000), (3363, 23100), (3383, 23200), (3406, 23300), (3429, 23400),
        (3433, 23500), (3443, 23600), (3454, 23700), (3462, 23800), (3467, 23900), (3491, 24000), (3503, 24100), (3511, 24200),
        (3515, 24300), (3527, 24400), (3531, 24500), (3533, 24600), (3572, 24700), (3643, 25300), (3644, 25600), (3685, 26200),
        (3687, 26500), (3701, 26800), (3704, 27100), (3739, 27700), (3740, 28000), (3741, 28300), (3743, 28900), (3749, 29200),
        (3757, 29500), (3781, 30100), (3787, 30400), (3793, 31000), (3867, 31500), (3873, 32500), (3941, 33000), (3951, 34000),
        (3980, 34500), (3987, 35500), (4087, 36000), (4097, 36500), (4153, 37000), (4154, 37500), (4217, 38000), (4677, 38100),
        (4804, 38400), (4816, 38500), (4974, 38600), (4990, 38800), (4992, 39000), (5089, 39400), (5091, 41800), (5195, 42100),
        (5197, 42800), (5364, 43100), (5457, 43800), (5574, 43900), (5581, 44000), (5667, 44400), (5691, 44600), (5725, 44800),
        (5747, 45000), (5755, 45200), (5759, 45400), (6037, 45900), (6166, 46400), (6271, 46900), (6491, 47400), (6633, 47600),
        (6636, 47700), (6639, 48400), (6641, 48900), (6695, 49400), (6733, 49900), (7443, 183), (7623, 84900), (7706, 183),
        (8072, 85000), (8093, 86000), (8102, 86100), (8174, 87100), (8191, 87400), (8193, 87700), (8195, 88000), (8198, 88300),
        (8211, 88600), (8217, 88900), (8225, 89200), (8241, 89500), (8292, 90300), (8345, 91300), (8347, 93300), (8367, 93400),
        (8414, 93500), (8421, 93900), (8447, 94900), (8575, 95000), (8580, 95100), (8585, 95200), (8669, 96200), (8742, 96300),
        (8767, 96400), (8772, 96500), (8781, 96600), (8791, 96700), (8863, 96800), (8892, 97800), (8906, 98600), (8915, 99600),
        (8917, 101600), (8933, 102400), (8991, 102700), (8997, 103000), (9046, 104000), (9115, 104300), (9117, 104600),
    ];

    [Fact]
    public void La_trace_reelle_du_19XX_donne_toute_la_partie_en_un_seul_run()
    {
        var run = NelfePlayScoringReporter.SelectBestRun(Reel19xx);
        Assert.Equal(Reel19xx[0], run[0]);
        Assert.Equal(104600, run[^1].total);
        Assert.DoesNotContain(run, p => p.total == 183);
        Assert.True(run.Zip(run.Skip(1), (a, b) => b.total >= a.total).All(x => x), "le run est monotone");
    }

    [Fact]
    public void Avec_le_run_entier_le_1LC_est_coupe_a_la_premiere_vie_perdue_et_PerteSansPlace_se_tait()
    {
        var run = NelfePlayScoringReporter.SelectBestRun(Reel19xx);
        var (premiere, mort) = PremiereVie.Couper(run, [(2541L, 1), (5087L, 1)]);
        Assert.Equal(2541, mort);
        Assert.Equal(5600, premiere[^1].total);   // le score a la perte de la premiere vie (image 2541), pas celui de la partie entiere
        Assert.Null(PremiereVie.PerteSansPlace(run, [(2541L, 1), (5087L, 1)], Reel19xx, [726L]));
    }

    [Fact]
    public void Une_baisse_qui_retrouve_le_niveau_d_avant_n_est_pas_une_nouvelle_partie()
    {
        List<(long frame, long total)> t = [(100, 1000), (200, 5000), (300, 20000), (700, 100), (800, 100), (900, 21000), (1000, 21500)];
        Assert.Equal(21500, NelfePlayScoringReporter.SelectBestRun(t)[^1].total);
    }

    [Fact]
    public void Une_vraie_remise_a_zero_qui_remonte_lentement_reste_une_nouvelle_partie()
    {
        // Premiere partie a 20 000, GAME OVER, nouvelle partie : 0, 100, 200... jusqu'a 30 000 (elle depasse la premiere).
        var t = new List<(long frame, long total)> { (100, 1000), (200, 5000), (300, 20000), (700, 0), (720, 100), (740, 300) };
        for (var k = 0; k < 40; k++) t.Add((800 + k * 100, 300 + (k + 1) * 800L));
        var run = NelfePlayScoringReporter.SelectBestRun(t);
        Assert.Equal(700, run[0].frame);
        Assert.Equal(t[^1].total, run[^1].total);
    }

    [Fact]
    public void Une_chute_sans_retour_dans_les_quatre_lectures_reste_une_nouvelle_partie()
    {
        List<(long frame, long total)> t = [(100, 1000), (200, 20000), (300, 30000), (400, 0), (410, 10), (420, 20), (430, 30), (440, 40), (450, 31000), (460, 31500)];
        var run = NelfePlayScoringReporter.SelectBestRun(t);
        Assert.Equal(400, run[0].frame);
    }

    [Fact]
    public void Une_chute_a_zero_suivie_d_un_bond_au_niveau_d_avant_reste_une_nouvelle_partie()
    {
        List<(long frame, long total)> t = [(100, 500), (200, 1900), (300, 0), (400, 2500)];
        Assert.Equal(300, NelfePlayScoringReporter.SelectBestRun(t)[0].frame);
    }

    [Fact]
    public void Un_petit_score_n_est_jamais_touche()
    {
        List<(long frame, long total)> t = [(100, 100), (200, 600), (300, 900), (400, 0), (500, 1000), (600, 1100)];
        Assert.Equal(400, NelfePlayScoringReporter.SelectBestRun(t)[0].frame);
    }
}
