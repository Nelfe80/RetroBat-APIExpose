using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// UN SERVICE QUI TOMBE AU DÉMARRAGE NE DOIT PAS EMPORTER L'API.
///
/// Le host .NET démarre ses services hébergés et, à la première exception, abandonne : le
/// processus s'arrête. Trente-neuf services, et il suffit que l'un d'eux échoue une fois pour que
/// la borne entière perde son API. Un exploitant a signalé le 24 septembre 2026 « quand je quitte
/// un jeu l'API se déconnecte, je dois redémarrer RetroBat à chaque fois » ; son diagnostic porte
/// une <c>SocketException (10054)</c> — une connexion fermée par le pair — remontée à travers
/// <c>Host.StartAsync</c>. Une coupure réseau passagère suffisait donc à tuer le scoring.
///
/// Le pire, c'est que ça ne laissait rien : le service fautif n'était pas nommé, et l'API mourait
/// avant d'avoir pu écrire quoi que ce soit d'utile. Cette enveloppe change les deux : la panne
/// est CONTENUE au service concerné, et elle est NOMMÉE dans le journal.
///
/// Ce qui n'est pas attrapé : l'annulation, qui est un arrêt demandé et doit remonter telle quelle.
/// </summary>
public static class HostedServiceGuard
{
    /// <summary>
    /// Enveloppe tous les services hébergés déjà enregistrés. À appeler juste avant
    /// <c>builder.Build()</c>, quand la liste est complète.
    /// </summary>
    public static IServiceCollection ProtegerLesServicesHeberges(this IServiceCollection services)
    {
        var descripteurs = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
        foreach (var d in descripteurs)
        {
            var index = services.IndexOf(d);
            services[index] = ServiceDescriptor.Describe(
                typeof(IHostedService),
                sp => new ServiceProtege(Instancier(sp, d), sp.GetService<ILoggerFactory>()),
                d.Lifetime);
        }

        return services;
    }

    private static IHostedService Instancier(IServiceProvider sp, ServiceDescriptor d)
    {
        if (d.ImplementationInstance is IHostedService deja)
        {
            return deja;
        }

        if (d.ImplementationFactory is not null)
        {
            return (IHostedService) d.ImplementationFactory(sp);
        }

        return (IHostedService) ActivatorUtilities.CreateInstance(sp, d.ImplementationType!);
    }

    /// <summary>Le service, et son échec transformé en ligne de journal.</summary>
    private sealed class ServiceProtege : IHostedService
    {
        private readonly IHostedService _vrai;
        private readonly ILogger? _logger;

        public ServiceProtege(IHostedService vrai, ILoggerFactory? fabrique)
        {
            _vrai = vrai;
            _logger = fabrique?.CreateLogger("RetroBat.Api.Demarrage");
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _vrai.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;   // un arret demande n'est pas une panne
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "Le service {Service} n'a pas demarre. L'API continue SANS lui : ce qu'il porte "
                    + "sera indisponible, mais le reste de la borne fonctionne.",
                    _vrai.GetType().Name);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _vrai.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A l'arret, une exception n'a plus rien a proteger : on la note et on laisse
                // les autres services se fermer proprement.
                _logger?.LogWarning(ex, "Le service {Service} s'est mal arrete.", _vrai.GetType().Name);
            }
        }
    }
}
