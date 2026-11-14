using RetroBat.Api.Deck;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

[CollectionDefinition("LaunchIdentity", DisableParallelization = true)]
public sealed class LaunchIdentityCollection { }

/// <summary>
/// ECR-08 (14/11) : un identifiant de lancement dans ui.game.started et ui.game.ended. Un second depart du meme jeu avant
/// sa fin (lancement direct puis crochet d'ES) garde le meme ; un jeu relance apres sa fin en a un nouveau.
/// </summary>
[Collection("LaunchIdentity")]
public sealed class LaunchIdentityTests : IDisposable
{
    public LaunchIdentityTests() => LaunchIdentity.Reset();

    public void Dispose() => LaunchIdentity.Reset();

    [Fact]
    public void Un_second_depart_du_meme_jeu_garde_l_identifiant()
    {
        var t0 = new DateTime(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);
        var direct = LaunchIdentity.Open(@"E:\RetroBat\roms\fbneo\19xx.zip", t0);
        var crochet = LaunchIdentity.Open("./19XX.zip", t0.AddSeconds(30));
        Assert.Equal(32, direct.Length);
        Assert.Equal(direct, crochet);
        Assert.Equal(direct, LaunchIdentity.Current);
    }

    [Fact]
    public void Apres_la_fin_le_meme_jeu_a_un_nouvel_identifiant()
    {
        var premier = LaunchIdentity.Open("sf2.zip");
        Assert.Equal(premier, LaunchIdentity.Close());
        Assert.Null(LaunchIdentity.Current);
        Assert.Null(LaunchIdentity.Close());   // une seconde fin ne rend rien
        Assert.NotEqual(premier, LaunchIdentity.Open("sf2.zip"));
    }

    [Fact]
    public void Un_autre_jeu_ou_une_fin_perdue_ouvre_un_nouveau_lancement()
    {
        var t0 = DateTime.UtcNow;
        var a = LaunchIdentity.Open("sf2.zip", t0);
        var b = LaunchIdentity.Open("mslug.zip", t0.AddSeconds(1));
        Assert.NotEqual(a, b);
        var c = LaunchIdentity.Open("mslug.zip", t0.AddMinutes(5));   // au-dela de la fenetre : une fin a ete perdue
        Assert.NotEqual(b, c);
    }

    [Fact]
    public async Task Le_Deck_reprend_l_identifiant_du_depart()
    {
        var bus = new SimpleEventBus();
        var deck = new DeckStateService(bus, new ApiContext());
        await deck.StartAsync(CancellationToken.None);
        try
        {
            await bus.PublishAsync(new EventEnvelope
            {
                Type = "ui.game.started",
                Payload = new { SystemId = "fbneo", GamePath = @"E:\RetroBat\roms\fbneo\19xx.zip", LaunchId = "0123456789abcdef0123456789abcdef" },
            });
            Assert.Equal("0123456789abcdef0123456789abcdef", deck.LaunchIdCourant);
            Assert.Equal("0123456789abcdef0123456789abcdef", deck.Snapshot("test").Game!.LaunchId);

            // sans identifiant (ancien chemin), le Deck en tire un, comme avant
            await bus.PublishAsync(new EventEnvelope { Type = "ui.game.ended", Payload = new { } });
            await bus.PublishAsync(new EventEnvelope { Type = "ui.game.started", Payload = new { SystemId = "fbneo", GamePath = @"E:\RetroBat\roms\fbneo\sf2.zip" } });
            Assert.Equal(32, deck.LaunchIdCourant!.Length);
            Assert.NotEqual("0123456789abcdef0123456789abcdef", deck.LaunchIdCourant);
        }
        finally
        {
            await deck.StopAsync(CancellationToken.None);
        }
    }
}
