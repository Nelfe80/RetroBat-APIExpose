using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Une lecture declaree BCD dont un quartet vaut A-F n'est pas un score. Vecu le 2026-09-22 sur
/// Ms. Pac-Man sous MAME standalone : deux lectures de RAM non initialisee (0xF0C090) au
/// demarrage entraient dans la trajectoire comme 15 777 936, et « le meilleur run » retenait ce
/// pic a la place du 680 reel. L'echantillon doit etre ignore, pas lu en binaire.
/// </summary>
public sealed class LiveScoreBcdSampleTests
{
    [Fact]
    public async Task Une_lecture_bcd_invalide_est_ignoree_et_le_score_reste_le_dernier_lisible()
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

        // 680 en BCD sur trois octets : 0x000680.
        await bus.PublishAsync(Echantillon(0x000680, "0x000680"));
        // RAM pas encore initialisee : quartets F et C, aucun chiffre.
        await bus.PublishAsync(Echantillon(0xF0C090, "0xF0C090"));
        // Le jeu continue : 1 250.
        await bus.PublishAsync(Echantillon(0x001250, "0x001250"));

        Assert.Contains(680L, totaux);
        Assert.Contains(1250L, totaux);
        Assert.DoesNotContain(15777936L, totaux);
        Assert.DoesNotContain(0L, totaux);
    }

    private static EventEnvelope Echantillon(long valeur, string hex) => new()
    {
        Type = "retroarch.memory.changed",
        Payload = new
        {
            SystemId = "arcade",
            Rom = "ms-pac-man",
            Source = "wrapper",
            Signal = new
            {
                Name = "SCORE_STATE",
                Channel = "SCORE",
                Address = "0X4E80",
                Value = valeur,
                RawValueHex = hex,
                // Sans fichier .MEM sous la main, c'est la description qui declare le BCD.
                SourceDescription = "Player 1 score (BCD)",
            },
        },
    };
}
