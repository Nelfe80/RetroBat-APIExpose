namespace RetroBat.Api.Infrastructure;

/// <summary>
/// (brief du 13/11, PERF-API-01) Un travail de demarrage qui n'a pas a retarder l'ecoute d'EmulationStation.
///
/// L'hote .NET lance le <c>StartAsync</c> de chaque service l'un apres l'autre, dans l'ordre d'inscription, et le
/// fournisseur d'ES est inscrit apres eux : tout travail attendu dans un <c>StartAsync</c> retarde d'autant le suivi
/// des selections. Mesure sur la borne le 14/11 a 18:15 : verification des 3 425 fichiers de l'installeur 5,9 s,
/// menu d'ES 1,3 s, pour 0 fichier change. Ces verifications partent maintenant une fois l'hote demarre
/// (<see cref="IHostApplicationLifetime.ApplicationStarted"/>), en arriere-plan, et s'arretent avec l'API.
/// </summary>
public static class ApresLeDemarrage
{
    public static void Lancer(IHostApplicationLifetime? vie, ILogger? journal, string quoi, Func<CancellationToken, Task> travail)
    {
        if (vie is null)
        {
            _ = Task.Run(() => ExecuterAsync(journal, quoi, travail, CancellationToken.None), CancellationToken.None);
            return;
        }

        // Deja demarre : Register appelle tout de suite.
        vie.ApplicationStarted.Register(() =>
            _ = Task.Run(() => ExecuterAsync(journal, quoi, travail, vie.ApplicationStopping), CancellationToken.None));
    }

    private static async Task ExecuterAsync(ILogger? journal, string quoi, Func<CancellationToken, Task> travail, CancellationToken arret)
    {
        try
        {
            await travail(arret).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (arret.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            journal?.LogWarning(ex, "Demarrage : {Quoi} a echoue en arriere-plan.", quoi);
        }
    }
}
