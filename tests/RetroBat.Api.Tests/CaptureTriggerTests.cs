using RetroBat.Api.Scoring.Discovery;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les quatre freins d'A1.11 : hors declenchement, rien ne se copie, et meme en
/// declenchement, pas plus que ce qui est necessaire.
/// </summary>
public class CaptureTriggerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 1, 0, 0, TimeSpan.Zero);

    private static CaptureTrigger Trigger(ScoringDiscoveryOptions? options = null) => new(options ?? new ScoringDiscoveryOptions());

    [Fact]
    public void Sans_changement_de_score_rien_ne_s_arme_jamais()
    {
        var trigger = Trigger();

        // Une heure de jeu tranquille : aucune image ne doit etre demandee.
        for (var i = 0; i < 3600; i++)
        {
            Assert.False(trigger.Poll(T0.AddSeconds(i)).Arm);
        }
    }

    [Fact]
    public void Un_score_qui_vient_de_changer_n_est_pas_encore_un_score()
    {
        var trigger = Trigger();
        trigger.OnScoreChanged(1200, T0);

        Assert.False(trigger.Poll(T0.AddMilliseconds(299)).Arm);
    }

    [Fact]
    public void Un_score_pose_arme_la_rafale_du_CDC()
    {
        var trigger = Trigger();
        trigger.OnScoreChanged(1200, T0);

        var decision = trigger.Poll(T0.AddMilliseconds(300));

        Assert.True(decision.Arm);
        Assert.Equal(1200, decision.Value);
        Assert.Equal(3, decision.Frames);
    }

    [Fact]
    public void Un_score_qui_defile_repousse_l_armement_et_seule_la_derniere_valeur_compte()
    {
        // Un compteur qui monte par paliers de 100 pendant une seconde : on ne veut pas
        // quatre captures de valeurs deja perimees, on veut celle qui reste affichee.
        var trigger = Trigger();
        trigger.OnScoreChanged(100, T0);
        trigger.OnScoreChanged(200, T0.AddMilliseconds(200));
        trigger.OnScoreChanged(300, T0.AddMilliseconds(400));
        trigger.OnScoreChanged(400, T0.AddMilliseconds(600));

        Assert.False(trigger.Poll(T0.AddMilliseconds(700)).Arm);

        var decision = trigger.Poll(T0.AddMilliseconds(900));
        Assert.True(decision.Arm);
        Assert.Equal(400, decision.Value);
    }

    [Fact]
    public void Le_meme_score_ne_declenche_pas_deux_fois()
    {
        var trigger = Trigger();
        trigger.OnScoreChanged(1200, T0);
        Assert.True(trigger.Poll(T0.AddMilliseconds(300)).Arm);

        // Il faudra que le score change a nouveau : c'est justement ce que le suivi
        // temporel veut observer.
        Assert.False(trigger.Poll(T0.AddSeconds(10)).Arm);
    }

    [Fact]
    public void Deux_captures_ne_se_suivent_pas_de_plus_pres_que_l_intervalle()
    {
        var trigger = Trigger();
        trigger.OnScoreChanged(100, T0);
        Assert.True(trigger.Poll(T0.AddMilliseconds(300)).Arm);

        trigger.OnScoreChanged(200, T0.AddMilliseconds(310));
        Assert.False(trigger.Poll(T0.AddMilliseconds(700)).Arm);   // 400 ms apres la precedente
        Assert.True(trigger.Poll(T0.AddMilliseconds(801)).Arm);    // 501 ms apres
    }

    [Fact]
    public void Au_dela_du_plafond_la_minute_se_tait()
    {
        var options = new ScoringDiscoveryOptions { MaxCapturesPerMinute = 3, MinIntervalMs = 0, StableDelayMs = 0 };
        var trigger = Trigger(options);

        for (var i = 1; i <= 3; i++)
        {
            trigger.OnScoreChanged(i * 100, T0.AddSeconds(i));
            Assert.True(trigger.Poll(T0.AddSeconds(i)).Arm);
        }

        trigger.OnScoreChanged(400, T0.AddSeconds(4));
        var refused = trigger.Poll(T0.AddSeconds(4));
        Assert.False(refused.Arm);
        Assert.Contains("plafond", refused.Reason);
        Assert.Equal(3, trigger.CapturesThisMinute);
    }

    [Fact]
    public void La_minute_suivante_repart()
    {
        var options = new ScoringDiscoveryOptions { MaxCapturesPerMinute = 1, MinIntervalMs = 0, StableDelayMs = 0 };
        var trigger = Trigger(options);
        trigger.OnScoreChanged(100, T0);
        Assert.True(trigger.Poll(T0).Arm);

        trigger.OnScoreChanged(200, T0.AddSeconds(30));
        Assert.False(trigger.Poll(T0.AddSeconds(30)).Arm);

        trigger.OnScoreChanged(300, T0.AddSeconds(61));
        Assert.True(trigger.Poll(T0.AddSeconds(61)).Arm);
    }

    [Fact]
    public void Une_derive_de_duree_d_image_arrete_la_capture_pour_la_partie()
    {
        var trigger = Trigger();
        trigger.ReportFrameTimeDrift(7.5);

        Assert.True(trigger.DisarmedForSession);
        Assert.Contains("derive", trigger.DisarmReason);

        trigger.OnScoreChanged(1200, T0);
        Assert.False(trigger.Poll(T0.AddSeconds(5)).Arm);
    }

    [Fact]
    public void Une_derive_sous_le_seuil_ne_change_rien()
    {
        var trigger = Trigger();
        trigger.ReportFrameTimeDrift(4.9);

        Assert.False(trigger.DisarmedForSession);
        trigger.OnScoreChanged(1200, T0);
        Assert.True(trigger.Poll(T0.AddMilliseconds(300)).Arm);
    }

    [Fact]
    public void Un_desarmement_definitif_ne_se_rouvre_pas_a_la_minute_suivante()
    {
        // Une partie qui a ralenti une fois ne redevient pas un bon terrain de mesure.
        var trigger = Trigger();
        trigger.ReportFrameTimeDrift(20);

        trigger.OnScoreChanged(1200, T0);
        Assert.False(trigger.Poll(T0.AddMinutes(5)).Arm);
    }

    [Fact]
    public void Une_nouvelle_partie_repart_de_zero_meme_apres_un_desarmement()
    {
        var trigger = Trigger();
        trigger.DisarmForSession("essai");
        trigger.StartSession();

        Assert.False(trigger.DisarmedForSession);
        trigger.OnScoreChanged(1200, T0);
        Assert.True(trigger.Poll(T0.AddMilliseconds(300)).Arm);
    }

    [Fact]
    public void Un_score_negatif_est_ignore()
    {
        // Une lecture memoire qui part en vrille ne doit pas declencher de capture.
        var trigger = Trigger();
        trigger.OnScoreChanged(-1, T0);

        Assert.False(trigger.Poll(T0.AddSeconds(1)).Arm);
    }

    [Fact]
    public void La_raison_du_refus_est_toujours_nommee()
    {
        var trigger = Trigger();

        Assert.Equal("rien en attente", trigger.Poll(T0).Reason);
        trigger.OnScoreChanged(1, T0);
        Assert.Contains("pas encore pose", trigger.Poll(T0).Reason);
    }
}
