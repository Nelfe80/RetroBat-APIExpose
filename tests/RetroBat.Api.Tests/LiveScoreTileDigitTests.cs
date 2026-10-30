using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un score range chiffre par chiffre, tel qu'il est dessine a l'ecran : les zeros de tete sont
/// une tuile vide (0x80 sur Shadow Warriors, 0x24 sur Galaga), pas un nombre. Lue telle quelle,
/// la case des centaines de milliers valait 12 800 000 pour un 100 a l'ecran.
/// </summary>
public sealed class LiveScoreTileDigitTests
{
    [Theory]
    [InlineData(0x80)]
    [InlineData(0x24)]
    public async Task Une_tuile_vide_compte_pour_zero(int vide)
    {
        var bus = new SimpleEventBus();
        var totaux = new List<long>();
        bus.Subscribe<EventEnvelope>(e =>
        {
            if (!string.Equals(e.Type, "score.live.changed", StringComparison.OrdinalIgnoreCase)) return;
            var score = e.Payload?.GetType().GetProperty("Score")?.GetValue(e.Payload);
            if (score is long l) totaux.Add(l);
            else if (score is int i) totaux.Add(i);
        });
        var provider = new LiveScoreAggregatorProvider(bus, NullLogger<LiveScoreAggregatorProvider>.Instance);
        await provider.StartAsync();

        await bus.PublishAsync(Chiffre("0X62E38", vide, "hundred thousands (value * 100000)"));
        await bus.PublishAsync(Chiffre("0X62E39", vide, "ten thousands (value * 10000)"));
        await bus.PublishAsync(Chiffre("0X62E3A", vide, "thousands (value * 1000)"));
        await bus.PublishAsync(Chiffre("0X62E3B", 1, "hundreds (value * 100)"));
        await bus.PublishAsync(Chiffre("0X62E3C", 2, "tens (value * 10)"));
        await bus.PublishAsync(Chiffre("0X62E3D", 0, "units"));

        Assert.Equal(120L, totaux[^1]);
    }

    private static EventEnvelope Chiffre(string adresse, int valeur, string place) => new()
    {
        Type = "retroarch.memory.changed",
        Payload = new
        {
            SystemId = "arcade",
            Rom = "ninja-gaiden",
            Source = "wrapper",
            Signal = new
            {
                Name = "SCORE_STATE",
                Channel = "SCORE",
                Address = adresse,
                Value = (long)valeur,
                RawValueHex = "0x" + valeur.ToString("X2"),
                SourceDescription = "Player 1 score tile digit, " + place,
            },
        },
    };
}
