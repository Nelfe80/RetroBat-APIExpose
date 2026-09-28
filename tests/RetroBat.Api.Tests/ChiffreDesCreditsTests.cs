using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// 19xx et Metal Slug 3 ecrivent les continues dans le dernier chiffre du score (29 700 → 30 001,
/// 1 324 759 publie tel quel le 2026-09-27). La borne ne coupe jamais la partie : elle previent au
/// premier +1 et soumet la partie d'avant le continue, comme la plateforme le recalcule.
/// </summary>
public sealed class ChiffreDesCreditsTests
{
    private static List<(long frame, long total)> Traj(params long[] totaux)
        => totaux.Select((t, i) => ((long) i * 60, t)).ToList();

    [Fact]
    public void Une_partie_19xx_continuee_s_arrete_au_score_d_avant_le_continue()
    {
        var avant = NelfePlayScoringReporter.SansLesContinues(Traj(0, 500, 12000, 29700, 30001, 36801));
        Assert.Equal(29700, avant[^1].total);
        Assert.Equal(4, avant.Count);
    }

    [Fact]
    public void Une_partie_sans_continue_est_soumise_entiere()
    {
        var traj = Traj(0, 500, 12000, 29700);
        Assert.Equal(traj, NelfePlayScoringReporter.SansLesContinues(traj));
    }

    [Fact]
    public void Les_lectures_parasites_du_debut_ne_passent_pas_pour_un_continue()
    {
        // Metal Slug 3 : 63 avant la premiere lecture propre, puis la partie.
        var avant = NelfePlayScoringReporter.SansLesContinues(Traj(63, 65, 500, 1300, 1301, 2401));
        Assert.Equal(1300, avant[^1].total);
    }

    [Fact]
    public void Un_score_qui_retombe_sans_finir_par_zero_n_est_pas_un_continue()
    {
        // Une lecture basse puis une nouvelle partie : c'est le decoupage en runs qui tranche.
        var traj = Traj(0, 500, 3000, 7, 0, 800);
        Assert.Equal(traj, NelfePlayScoringReporter.SansLesContinues(traj));
    }

    [Fact]
    public void Une_nouvelle_partie_apres_un_continue_concourt_normalement()
    {
        // 19xx continue a 29 700, game over, puis une nouvelle partie dans la meme session.
        var gardees = NelfePlayScoringReporter.SansLesContinues(Traj(0, 500, 29700, 30001, 36801, 0, 800, 12000));
        Assert.Equal(new long[] { 0, 500, 29700, 0, 800, 12000 }, gardees.Select(p => p.total).ToArray());
    }

    [Fact]
    public void Un_seul_compteur_de_vies_decide_en_direct()
    {
        // 19xx : 2, 1, 0 puis 2 au continue, sur une seule adresse.
        var vies = new List<EvenementDeVie>
        {
            new("0xff82ec", false, 2, 10, 1),
            new("0xff82ec", true, 1, 100, 1),
            new("0xff82ec", true, 0, 200, 1),
            new("0xff82ec", false, 2, 300, 1),
        };
        Assert.Equal(1, NelfePlayScoringReporter.FinsSurLesVies(vies));

        // Un second compteur a parle (un drapeau de boss range dans le bloc des vies) : la fin de
        // partie choisira, on ne coupe rien en direct.
        vies.Add(new EvenementDeVie("0x315d", true, 0, 250, 1));
        Assert.Equal(0, NelfePlayScoringReporter.FinsSurLesVies(vies));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0L, false)]
    [InlineData(1500L, true)]
    public void Pas_de_continue_avant_le_premier_point(long? score, bool attendu)
        => Assert.Equal(attendu, NelfePlayScoringReporter.ContinueAProteger(score));

    [Theory]
    [InlineData(29700L, 29700L, 30001L, true)]
    [InlineData(29700L, 29700L, 29800L, false)]
    [InlineData(null, 63L, 65L, false)]
    [InlineData(29700L, 30001L, 30001L, false)]
    public void Le_continue_est_la_premiere_lecture_qui_monte_sans_finir_par_zero(long? propre, long? precedente, long total, bool attendu)
        => Assert.Equal(attendu, NelfePlayScoringReporter.EstUnContinue(propre, precedente, total));

    [Theory]
    [InlineData("fr", "1 324 750")]
    [InlineData("es", "1.324.750")]
    [InlineData("en", "1,324,750")]
    [InlineData("ja", "1,324,750")]
    public void Le_score_s_ecrit_comme_on_le_lit(string langue, string attendu)
        => Assert.Equal(attendu, NelfePlayScoringReporter.ScoreAffiche(1324750, langue));

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("ja")]
    [InlineData("zh")]
    [InlineData("ko")]
    public void Le_bandeau_existe_dans_chaque_langue_et_porte_le_score(string langue)
    {
        Assert.Contains("{0}", CabinetAnnounceText.Get("scoring_continue_title", langue));
        Assert.NotEqual("scoring_continue_sub", CabinetAnnounceText.Get("scoring_continue_sub", langue));
        Assert.NotNull(CabinetAnnounceText.Find("scoring_continue_title", langue));
    }
}
