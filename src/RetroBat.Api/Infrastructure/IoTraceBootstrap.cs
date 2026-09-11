namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Donne un journal aux classes STATIQUES qui lisent de gros fichiers.
///
/// `GamelistIdentity` est statique par necessite (elle est appelee depuis des chemins qui n'ont pas
/// d'injection), et sans journal ses balayages de plusieurs mega-octets se faisaient en silence.
/// Ce service ne fait que lui en poser un au demarrage.
/// </summary>
public sealed class IoTraceBootstrap : IHostedService
{
    public IoTraceBootstrap(ILogger<IoTraceBootstrap> logger)
    {
        GamelistIdentity.Journal = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
