using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un signal de demo en pleine partie, avec des appuis du joueur avant et apres, est ignore (regle user
/// 2026-10-06). Le .MEM d'Altered Beast declarait DEMO_MODE sur l'octet du niveau : chez une borne qui
/// ne voyait ni le START ni le credit, la partie s'arretait a la fin du niveau 1.
/// </summary>
public sealed class DemoEnPleinePartieTests
{
    private static readonly DateTime T0 = new(2026, 10, 6, 10, 26, 0, DateTimeKind.Utc);

    [Fact]
    public void Un_signal_juste_apres_des_appuis_est_mis_en_doute()
    {
        // Fin du niveau 1 d'Altered Beast : le joueur appuyait encore il y a 8 s (la cinematique).
        Assert.True(NelfePlayScoringReporter.DemoDouteuse(enJeu: false, dejaEnDemo: false, gameOverVu: false,
            dernierAppui: T0.AddSeconds(-8), maintenant: T0));
    }

    [Fact]
    public void Sans_appui_recent_la_demo_est_crue_tout_de_suite()
    {
        // L'attract du lancement : personne n'a encore touche a rien.
        Assert.False(NelfePlayScoringReporter.DemoDouteuse(false, false, false, DateTime.MinValue, T0));
        Assert.False(NelfePlayScoringReporter.DemoDouteuse(false, false, false, T0.AddSeconds(-21), T0));
    }

    [Fact]
    public void Apres_un_game_over_la_demo_est_vraie()
    {
        // Le joueur qui tapote pendant l'ecran de fin ne transforme pas l'attract en partie.
        Assert.False(NelfePlayScoringReporter.DemoDouteuse(false, false, gameOverVu: true, T0.AddSeconds(-2), T0));
    }

    [Fact]
    public void En_jeu_ouvert_ou_deja_en_demo_rien_ne_change()
    {
        Assert.False(NelfePlayScoringReporter.DemoDouteuse(enJeu: true, false, false, T0.AddSeconds(-1), T0));
        Assert.False(NelfePlayScoringReporter.DemoDouteuse(false, dejaEnDemo: true, false, T0.AddSeconds(-1), T0));
    }

    [Fact]
    public void Un_appui_dans_les_15_s_qui_suivent_refute_la_demo()
    {
        Assert.True(NelfePlayScoringReporter.DemoRefutee(T0, T0.AddSeconds(3)));
        Assert.True(NelfePlayScoringReporter.DemoRefutee(T0, T0.AddSeconds(15)));
        Assert.False(NelfePlayScoringReporter.DemoRefutee(T0, T0.AddSeconds(16)));
        // Un appui d'AVANT le signal ne compte pas pour l'apres.
        Assert.False(NelfePlayScoringReporter.DemoRefutee(T0, T0.AddSeconds(-1)));
    }

    [Fact]
    public void Sans_appui_apres_le_delai_la_demo_se_confirme()
    {
        Assert.True(NelfePlayScoringReporter.DemoConfirmee(T0, dernierAppui: T0.AddSeconds(-5), maintenant: T0.AddSeconds(16)));
        // Pas encore : le delai court toujours.
        Assert.False(NelfePlayScoringReporter.DemoConfirmee(T0, T0.AddSeconds(-5), T0.AddSeconds(10)));
        // Un appui depuis le signal : elle ne se confirme jamais.
        Assert.False(NelfePlayScoringReporter.DemoConfirmee(T0, T0.AddSeconds(4), T0.AddSeconds(30)));
    }
}
