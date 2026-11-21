using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Scoring;
using RetroBat.Domain.Events;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA DEMO QUI SUIT LA PARTIE N'EST PAS DU JEU (21/11, DaiOuJou de la borne du user, API 1.9.206). Parti a 09:22:51 (credit
/// consomme), fini vers 09:24:23 (score retombe a zero), puis 35 minutes de demo : 2 554 070 etaient envoyes au site et un
/// « new record » partait dans le direct. Le .MEM n'a ni GAME_OVER ni ligne de demo qui parle : la fenetre de jeu se ferme
/// maintenant quand le score retombe a zero sans nouveau depart. Les appuis du joueur pendant la demo n'y changent rien.
/// </summary>
public sealed class DemoHorsFenetreDeJeuTests
{
    private sealed class SansReseau : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Muet());
        private sealed class Muet : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => throw new HttpRequestException("pas de reseau dans les tests");
        }
    }

    private static NelfePlayScoringReporter Rapporteur()
        => new(new SimpleEventBus(), new SansReseau(), new NelfePlayDeviceStore(NullLogger<NelfePlayDeviceStore>.Instance));

    private static async Task Score(NelfePlayScoringReporter r, long total, long image)
    {
        r.HandleEvent(new EventEnvelope { Type = "retroarch.score", Payload = new { Frame = image } });
        r.HandleEvent(new EventEnvelope { Type = "score.live.changed", Payload = new { Score = total, Player = 1, Frame = image } });
        await Task.Delay(70);   // une salve de score dure 50 ms
    }

    private static void Depart(NelfePlayScoringReporter r)
        => r.HandleEvent(new EventEnvelope { Type = "retroarch.state", Payload = new { actionType = "GAME_START" } });

    private static void Appui(NelfePlayScoringReporter r, string bouton)
        => r.HandleEvent(new EventEnvelope { Type = "panel.input.pressed", Payload = new { System = bouton, Identity = bouton, Player = 1, Device = 0 } });

    [Fact]
    public async Task La_demo_apres_la_partie_sort_de_la_fenetre_meme_quand_le_joueur_appuie_sans_credit_ni_depart()
    {
        var r = Rapporteur();
        Depart(r);
        await Score(r, 0, 100);
        await Score(r, 4_000, 400);
        await Score(r, 80_000, 900);
        await Score(r, 172_020, 1400);
        Assert.False(r.ScoreHorsPartie());

        // La fin : le score retombe a zero, puis la demo, avec des appuis (ils ne pilotent pas le jeu) et ni credit ni depart.
        await Score(r, 0, 1500);
        await Score(r, 80, 1600);
        await Score(r, 90, 1650);
        Assert.True(r.ScoreHorsPartie());
        Appui(r, "UP");
        foreach (var (total, image) in new[] { (398_910L, 1800L), (1_000_000L, 2000L), (2_146_410L, 2200L), (2_403_150L, 2300L), (2_554_070L, 2314L) })
        {
            Appui(r, "FIRE");
            await Score(r, total, image);
            Assert.True(r.ScoreHorsPartie());
        }

        var enJeu = r.TotauxEnJeuPourEssai();
        Assert.Equal(172_020, enJeu.Max());
        Assert.DoesNotContain(2_554_070L, enJeu);
    }

    [Fact]
    public async Task Une_partie_normale_reste_entiere_avec_un_continue_et_un_score_qui_ne_retombe_pas()
    {
        var r = Rapporteur();
        Depart(r);
        var attendus = new List<long>();
        long image = 100;
        foreach (var total in new long[] { 0, 1_000, 25_000, 90_000, 150_000, 420_000, 2_554_070 })
        {
            Appui(r, "LEFT");
            await Score(r, total, image += 300);
            attendus.Add(total);
            Assert.False(r.ScoreHorsPartie());
        }
        Assert.Equal(attendus, r.TotauxEnJeuPourEssai());
    }

    [Fact]
    public async Task Un_depart_avant_la_remise_a_zero_garde_la_partie_suivante_en_jeu()
    {
        // Le joueur relance pendant l'ecran de fin : le score retombe a zero apres SON depart, c'est sa nouvelle partie.
        var r = Rapporteur();
        Depart(r);
        await Score(r, 0, 100);
        await Score(r, 50_000, 800);
        Depart(r);   // image courante 800 : pas apres le point haut, mais le depart suit la derniere lecture
        r.HandleEvent(new EventEnvelope { Type = "retroarch.score", Payload = new { Frame = 900L } });
        Depart(r);   // depart a l'image 900, apres le point haut (800)
        await Score(r, 0, 950);
        await Score(r, 300, 1100);
        await Score(r, 900, 1300);
        Assert.False(r.ScoreHorsPartie());
        Assert.Contains(900L, r.TotauxEnJeuPourEssai());
    }

    [Fact]
    public async Task Sans_depart_vu_le_comportement_d_avant_est_garde()
    {
        var r = Rapporteur();
        await Score(r, 0, 100);
        await Score(r, 100_000, 800);
        await Score(r, 0, 900);
        await Score(r, 100, 1000);
        await Score(r, 2_000_000, 1200);
        Assert.False(r.ScoreHorsPartie());
        Assert.Contains(2_000_000L, r.TotauxEnJeuPourEssai());
    }

    [Fact]
    public void La_fin_de_fenetre_se_confirme_a_la_salve_suivante_encore_basse()
    {
        var f = new FinDeFenetreParBaisse();
        Assert.Null(f.Lire(0, 172_020, true, 1400));      // la baisse ouvre l'attente
        Assert.Null(f.Lire(10, 172_020, false, 1500));    // meme salve : rien
        Assert.Equal(1400, f.Lire(80, 0, true, 1600));    // la salve suivante confirme
        // Une retenue mal lue (un chiffre de poids fort) n'est pas une remise a zero.
        var g = new FinDeFenetreParBaisse();
        Assert.Null(g.Lire(1_157_820, 99_950_000, true, 10));
        Assert.Null(g.Lire(1_208_680, 1_157_820, true, 20));
    }

    [Theory]
    [InlineData(true, true, 100, 500, true)]    // depart avant le point haut : fin de partie
    [InlineData(true, true, 600, 500, false)]   // depart apres le point haut (relance, continue) : la fenetre reste
    [InlineData(false, true, 100, 500, false)]  // depart non vu par la machine : comportement d'avant
    [InlineData(true, false, 100, 500, false)]  // deja hors fenetre
    public void Quand_la_fenetre_se_ferme(bool departMachine, bool enJeu, long depart, long haut, bool attendu)
        => Assert.Equal(attendu, FinDeFenetreParBaisse.Ferme(departMachine, enJeu, depart, haut));
}
