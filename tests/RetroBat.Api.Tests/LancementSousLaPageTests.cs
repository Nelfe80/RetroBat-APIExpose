using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Speedrun;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'ECRAN D'INTRO DES SPEEDRUNS TOUT DE SUITE (13/11) : le lancement se coupe en controles rapides (<see cref="SpeedrunSessionService.Verifier"/>,
/// avant la page) et partie lente (<see cref="SpeedrunSessionService.Partir"/>, sous la page), et ses trois preparations independantes
/// (fantomes, Deck, manettes d'ES) partent ensemble. La page et la manette se verifient sur la borne ; ici, les regles.
/// </summary>
public class LancementSousLaPageTests
{
    private static readonly Reglement GreenHill1 = Reglement.Lire(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Reglements", "sonic-the-hedgehog.green-hill-1.json")));

    private static SpeedrunSessionService Service(ApiContext contexte, params Reglement[] reglements)
    {
        var catalogue = new CatalogueDesDefis();
        catalogue.Remplacer(reglements);
        return new SpeedrunSessionService(new SimpleEventBus(), contexte, NullLogger<SpeedrunSessionService>.Instance, catalogue: catalogue);
    }

    [Fact]
    public void Les_trois_preparations_partent_ensemble_et_le_lancement_attend_la_plus_longue()
    {
        // 12/11 09:19 sur la borne : fantomes 2,5 s + Deck 6,2 s + manettes 12,2 s, l'une apres l'autre
        var chrono = Stopwatch.StartNew();
        var (a, b, c) = SpeedrunSessionService.PreparerEnsemble(
            () => { Thread.Sleep(700); return true; },
            () => { Thread.Sleep(700); return "deck"; },
            () => { Thread.Sleep(700); return "-p1index 0"; });
        chrono.Stop();

        Assert.True(a);
        Assert.Equal("deck", b);
        Assert.Equal("-p1index 0", c);
        // ensemble : ~0,7 s ; l'une apres l'autre : 2,1 s
        Assert.InRange(chrono.ElapsedMilliseconds, 600, 1800);
    }

    [Fact]
    public void Une_preparation_qui_echoue_ressort_telle_quelle_une_fois_les_autres_finies()
    {
        var finie = false;
        var e = Assert.Throws<InvalidOperationException>(() => SpeedrunSessionService.PreparerEnsemble<bool, string?, string>(
            () => throw new InvalidOperationException("fantomes"),
            () => { Thread.Sleep(300); finie = true; return null; },
            () => ""));

        Assert.Equal("fantomes", e.Message);
        Assert.True(finie);
    }

    [Fact]
    public void Les_preparations_ne_se_bloquent_pas_entre_elles()
    {
        // les fantomes attendent leur budget en bloquant leur fil (travail.Wait) : le Deck et les manettes avancent quand meme
        using var porte = new ManualResetEventSlim();
        var (a, _, _) = SpeedrunSessionService.PreparerEnsemble(
            () => porte.Wait(TimeSpan.FromSeconds(5)),
            () => { porte.Set(); return 1; },
            () => 2);

        Assert.True(a);
    }

    [Fact]
    public void Un_defi_inconnu_est_refuse_avant_la_page()
    {
        var (verifie, refus) = Service(new ApiContext(), GreenHill1).Verifier("inconnu.niveau-1", (_, _) => "x.zip");

        Assert.Null(verifie);
        Assert.Equal(new ReponseLancement(false, "unknown_challenge"), refus);
    }

    [Fact]
    public void Un_jeu_en_cours_est_refuse_avant_la_page()
    {
        var contexte = new ApiContext();
        contexte.Ui.Running = new GameReference { SystemId = "megadrive", GamePath = @"E:\RetroBat\roms\megadrive\Sonic.zip" };

        var (verifie, refus) = Service(contexte, GreenHill1).Verifier(GreenHill1.ChallengeId, (_, _) => "x.zip");

        Assert.Null(verifie);
        Assert.Equal("game_running", refus?.Raison);
    }

    [Fact]
    public void Un_jeu_parti_pendant_la_page_passe_avant_et_rien_n_est_lance()
    {
        // la page a tenu quelques secondes ; ES a lance un jeu entre-temps : la partie lente ne prepare rien
        var contexte = new ApiContext();
        var service = Service(contexte, GreenHill1);
        contexte.Ui.Running = new GameReference { SystemId = "megadrive", GamePath = @"E:\RetroBat\roms\megadrive\Sonic.zip" };

        var reponse = service.Partir(new LancementVerifie(GreenHill1, @"E:\RetroBat\roms\megadrive\Sonic.zip", null), pages: true);

        Assert.Equal(new ReponseLancement(false, "game_running"), reponse);
    }
}
