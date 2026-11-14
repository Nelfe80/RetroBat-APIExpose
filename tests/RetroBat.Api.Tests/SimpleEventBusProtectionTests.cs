using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Events;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Brief du 13/11, ROB-01 : un try par abonne, une liste protegee.</summary>
public class SimpleEventBusProtectionTests
{
    [Fact]
    public async Task Un_abonne_qui_leve_ne_prive_pas_les_suivants()
    {
        var bus = new SimpleEventBus();
        var recus = new List<string>();
        using var a = bus.Subscribe<EventEnvelope>(e => recus.Add("a:" + e.Type));
        using var b = bus.Subscribe<EventEnvelope>(_ => throw new InvalidOperationException("panne"));
        using var c = bus.Subscribe<EventEnvelope>(e => recus.Add("c:" + e.Type));

        await bus.PublishAsync(new EventEnvelope { Type = "ingame.memory.changed" });

        Assert.Equal(new[] { "a:ingame.memory.changed", "c:ingame.memory.changed" }, recus);
        Assert.Equal(1, bus.Pannes);
    }

    [Fact]
    public async Task Desabonnement_retire_un_seul_abonne_et_reste_idempotent()
    {
        var bus = new SimpleEventBus();
        var compte = 0;
        Action<EventEnvelope> h = _ => compte++;
        var s1 = bus.Subscribe(h);
        var s2 = bus.Subscribe(h);

        s1.Dispose();
        s1.Dispose();
        await bus.PublishAsync(new EventEnvelope { Type = "x" });
        Assert.Equal(1, compte);

        s2.Dispose();
        await bus.PublishAsync(new EventEnvelope { Type = "x" });
        Assert.Equal(1, compte);
    }

    [Fact]
    public async Task Abonnements_concurrents_pendant_les_publications_ne_se_perdent_pas()
    {
        var bus = new SimpleEventBus();
        const int Abonnes = 400;
        var publier = true;
        var publications = Task.Run(async () =>
        {
            while (Volatile.Read(ref publier))
            {
                await bus.PublishAsync(new EventEnvelope { Type = "flux" });
            }
        });

        var recus = new int[Abonnes];
        Parallel.For(0, Abonnes, i => bus.Subscribe<EventEnvelope>(_ => Interlocked.Increment(ref recus[i])));
        Volatile.Write(ref publier, false);
        await publications;

        Array.Clear(recus);
        await bus.PublishAsync(new EventEnvelope { Type = "fin" });
        Assert.All(recus, n => Assert.Equal(1, n));
    }

    [Fact]
    public async Task Un_abonne_peut_se_desabonner_pendant_sa_propre_publication()
    {
        var bus = new SimpleEventBus();
        IDisposable? moi = null;
        var appels = 0;
        moi = bus.Subscribe<EventEnvelope>(_ => { appels++; moi!.Dispose(); });

        await bus.PublishAsync(new EventEnvelope { Type = "x" });
        await bus.PublishAsync(new EventEnvelope { Type = "x" });

        Assert.Equal(1, appels);
    }
}
