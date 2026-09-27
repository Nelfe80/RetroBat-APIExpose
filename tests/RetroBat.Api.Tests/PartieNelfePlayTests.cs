using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Hors NelfePlay, un jeu garde son propre comportement : ni scoring, ni replay (regle user
/// 2026-09-27). C'est l'endroit d'ou la partie est lancee qui decide, pas le jeu.
/// </summary>
public sealed class PartieNelfePlayTests
{
    private static async Task<PartieNelfePlayService> DemarrerAsync(SimpleEventBus bus, MediaRuntimeState media)
    {
        var partie = new PartieNelfePlayService(bus, media);
        await partie.StartAsync(CancellationToken.None);
        return partie;
    }

    private static Task LancerAsync(SimpleEventBus bus)
        => bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { GamePath = "roms/mame/19xx.zip" } });

    [Theory]
    [InlineData("nelfeplay-scoring", null, "collection")]
    [InlineData("NelfePlay-Scoring", null, "collection")]
    [InlineData("mame", null, null)]
    [InlineData("favorites", null, null)]
    [InlineData("", null, null)]
    [InlineData(null, null, null)]
    [InlineData("mame", "defi", "defi")]
    [InlineData("nelfeplay-scoring", "site", "site")]
    public void Le_carrousel_ou_une_fonction_NelfePlay_decide(string? carrousel, string? annonce, string? attendu)
        => Assert.Equal(attendu, PartieNelfePlayService.Juger(carrousel, annonce));

    [Theory]
    [InlineData("", true, "collection (carrousel inconnu)")]
    [InlineData(null, true, "collection (carrousel inconnu)")]
    [InlineData("", false, null)]
    [InlineData("", null, null)]
    [InlineData("mame", true, null)]   // le carrousel est connu : c'est lui qui decide
    public void Carrousel_inconnu_on_juge_le_jeu(string? carrousel, bool? dansLaCollection, string? attendu)
        => Assert.Equal(attendu, PartieNelfePlayService.Juger(carrousel, null, dansLaCollection));

    [Fact]
    public async Task Un_jeu_lance_depuis_la_collection_est_une_partie_NelfePlay()
    {
        var bus = new SimpleEventBus();
        var media = new MediaRuntimeState();
        var partie = await DemarrerAsync(bus, media);

        media.MarkCarouselSystem("nelfeplay-scoring");
        await LancerAsync(bus);

        Assert.True(partie.EstNelfePlay);
        Assert.Equal("collection", partie.Origine);
    }

    [Fact]
    public async Task Le_meme_jeu_lance_depuis_son_systeme_est_une_partie_du_joueur()
    {
        var bus = new SimpleEventBus();
        var media = new MediaRuntimeState();
        var partie = await DemarrerAsync(bus, media);

        media.MarkCarouselSystem("mame");
        await LancerAsync(bus);

        Assert.False(partie.EstNelfePlay);
        Assert.Null(partie.Origine);
    }

    [Fact]
    public async Task Une_annonce_ne_vaut_que_pour_le_lancement_qui_la_suit()
    {
        var bus = new SimpleEventBus();
        var media = new MediaRuntimeState();
        var partie = await DemarrerAsync(bus, media);
        media.MarkCarouselSystem("mame");

        partie.AnnoncerLancement("defi");
        await LancerAsync(bus);
        Assert.Equal("defi", partie.Origine);

        // Le joueur revient dans son systeme et relance le jeu lui-meme.
        await LancerAsync(bus);
        Assert.False(partie.EstNelfePlay);
    }

    [Fact]
    public async Task Le_verdict_tient_jusqu_au_lancement_suivant()
    {
        // La session du listener arrive en fin de partie, parfois apres le game-end.
        var bus = new SimpleEventBus();
        var media = new MediaRuntimeState();
        var partie = await DemarrerAsync(bus, media);
        media.MarkCarouselSystem("nelfeplay-scoring");
        await LancerAsync(bus);

        await bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended", Payload = new { } });
        media.MarkCarouselSystem("mame");

        Assert.True(partie.EstNelfePlay);
    }
}
