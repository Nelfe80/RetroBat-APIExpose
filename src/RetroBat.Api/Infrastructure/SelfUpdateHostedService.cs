using System.Runtime.Versioning;
using Microsoft.Extensions.Options;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Au lancement d'APIExpose : s'il existe une version plus recente, on la prend.
///
/// En ARRIERE-PLAN et apres un delai : le demarrage appartient au watcher EmulationStation et
/// aux fournisseurs, pas a une requete sortante. Et si la borne est occupee (un jeu, un replay)
/// on ne bouge pas, on regarde de nouveau plus tard : une mise a jour qui coupe une partie
/// serait pire que pas de mise a jour du tout.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SelfUpdateHostedService : IHostedService
{
    private readonly SelfUpdateService _update;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILogger<SelfUpdateHostedService> _logger;
    private readonly CancellationTokenSource _arret = new();

    public SelfUpdateHostedService(
        SelfUpdateService update,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<SelfUpdateHostedService> logger)
    {
        _update = update;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var opt = _options.CurrentValue.SelfUpdate;
        if (!opt.Enabled) return Task.CompletedTask;
        _ = Task.Run(() => BoucleAsync(_arret.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task BoucleAsync(CancellationToken ct)
    {
        var opt = _options.CurrentValue.SelfUpdate;
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, opt.StartupDelaySeconds)), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            opt = _options.CurrentValue.SelfUpdate;
            try
            {
                var etat = await _update.AppliquerAsync(force: false, ct).ConfigureAwait(false);
                if (etat.Error is { Length: > 0 })
                {
                    _logger.LogDebug("Mise a jour : {Erreur}", etat.Error);
                }
                else if (etat.Started)
                {
                    return;   // l'updater prend la main, cette API va s'arreter
                }
                else if (!etat.UpdateAvailable)
                {
                    _logger.LogInformation("APIExpose {Version} : deja a jour.", etat.Installed);
                    if (opt.IntervalHours <= 0) return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Mise a jour : verification impossible.");
            }

            // Rendez-vous plus tard : la borne etait occupee, ou une version paraitra d'ici la.
            var heures = opt.IntervalHours <= 0 ? 6 : opt.IntervalHours;
            try { await Task.Delay(TimeSpan.FromHours(heures), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _arret.Cancel();
        return Task.CompletedTask;
    }
}
