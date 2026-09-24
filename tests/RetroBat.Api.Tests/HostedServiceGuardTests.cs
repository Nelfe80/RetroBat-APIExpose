using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un exploitant a signalé le 24 septembre 2026 : « quand je quitte un jeu l'API se déconnecte, je
/// dois redémarrer RetroBat à chaque fois ». Son diagnostic porte une SocketException (10054)
/// remontée à travers <c>Host.StartAsync</c> — le host abandonne à la première exception, et le
/// processus s'arrête. Trente-neuf services hébergés, et un seul suffisait.
/// </summary>
public class HostedServiceGuardTests
{
    private sealed class QuiTombe : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => throw new InvalidOperationException("panne simulee");

        public Task StopAsync(CancellationToken ct) => throw new InvalidOperationException("panne a l'arret");
    }

    private sealed class QuiMarche : IHostedService
    {
        public bool Demarre { get; private set; }

        public Task StartAsync(CancellationToken ct)
        {
            Demarre = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class QuiSAnnule : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => throw new OperationCanceledException(ct);

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static IReadOnlyList<IHostedService> Proteges(Action<IServiceCollection> enregistrer)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        enregistrer(services);
        services.ProtegerLesServicesHeberges();
        return [.. services.BuildServiceProvider().GetServices<IHostedService>()];
    }

    [Fact]
    public async Task Un_service_qui_tombe_au_demarrage_n_arrete_plus_l_API()
    {
        var proteges = Proteges(s => s.AddHostedService<QuiTombe>());

        // Sans l'enveloppe, cet appel levait et le host abandonnait tout le processus.
        await proteges.Single().StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Un_service_qui_tombe_a_l_arret_ne_gene_pas_les_autres()
    {
        var proteges = Proteges(s => s.AddHostedService<QuiTombe>());
        await proteges.Single().StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Les_services_sains_demarrent_normalement()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var sain = new QuiMarche();
        services.AddSingleton<IHostedService>(sain);
        services.AddHostedService<QuiTombe>();
        services.ProtegerLesServicesHeberges();

        foreach (var s in services.BuildServiceProvider().GetServices<IHostedService>())
        {
            await s.StartAsync(CancellationToken.None);
        }

        Assert.True(sain.Demarre);
    }

    [Fact]
    public async Task Un_arret_demande_remonte_tel_quel()
    {
        // L'annulation n'est pas une panne : elle doit garder son sens, sinon un arret propre
        // passerait pour un service en echec.
        var proteges = Proteges(s => s.AddHostedService<QuiSAnnule>());
        using var annule = new CancellationTokenSource();
        await annule.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => proteges.Single().StartAsync(annule.Token));
    }

    [Fact]
    public void Le_nombre_de_services_ne_change_pas()
    {
        var proteges = Proteges(s =>
        {
            s.AddHostedService<QuiTombe>();
            s.AddHostedService<QuiMarche>();
        });

        Assert.Equal(2, proteges.Count);
    }
}
