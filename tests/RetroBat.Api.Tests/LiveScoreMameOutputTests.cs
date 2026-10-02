using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les sorties de MAME autonome ne disent pas toutes un score. Labo du 2026-10-02, Metal Slug 3 :
/// digit1 a digit4 sont les afficheurs LED des credits de la Neo-Geo MVS, en motifs 7 segments.
/// Lus comme un score, ils se melaient a celui du joueur 1 (501 a l'ecran, 79 publie).
/// </summary>
public sealed class LiveScoreMameOutputTests
{
    [Fact]
    public async Task Un_motif_7_segments_n_est_pas_un_score()
    {
        var (bus, totaux) = Bus();
        var provider = new LiveScoreAggregatorProvider(bus, NullLogger<LiveScoreAggregatorProvider>.Instance);
        await provider.StartAsync();

        // « 0 » (0x3F), « 3 » (0x4F), « 6 » (0x7D) sur les afficheurs des credits.
        await bus.PublishAsync(Sortie("mslug3", "digit1", 0x3F));
        await bus.PublishAsync(Sortie("mslug3", "digit3", 0x4F));
        await bus.PublishAsync(Sortie("mslug3", "digit4", 0x7D));

        Assert.Empty(totaux);
    }

    [Fact]
    public async Task Quand_le_pont_lua_mesure_les_sorties_ne_comptent_pas()
    {
        var (bus, totaux) = Bus();
        var arbitrage = new IngameSourceArbitrationService();
        var provider = new LiveScoreAggregatorProvider(bus, NullLogger<LiveScoreAggregatorProvider>.Instance, arbitrage);
        await provider.StartAsync();

        await bus.PublishAsync(Sortie("galaga", "score", 1200));
        Assert.Equal(new long[] { 1200 }, totaux);

        arbitrage.MarkMameLuaSessionStarted("arcade", "mslug3", @"C:\ram\arcade\metal-slug-3.MEM");
        await bus.PublishAsync(Sortie("mslug3", "score", 79));
        await bus.PublishAsync(Sortie("mslug3", "digit4", 5));
        Assert.Equal(new long[] { 1200 }, totaux);
    }

    private static (SimpleEventBus Bus, List<long> Totaux) Bus()
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
        return (bus, totaux);
    }

    private static EventEnvelope Sortie(string machine, string cle, long valeur) => new()
    {
        Type = "mame.output.changed",
        Payload = new
        {
            Source = "mame.network",
            MachineName = machine,
            Signals = new[] { new { Key = cle, Value = valeur } },
        },
    };
}
