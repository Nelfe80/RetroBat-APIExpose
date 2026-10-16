using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

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
    private readonly IEventBus _bus;
    private readonly CancellationTokenSource _arret = new();
    // Une mise a jour remise parce qu'un jeu tournait : la fin du jeu reveille la boucle.
    private readonly SemaphoreSlim _finDeJeu = new(0, 1);
    private volatile bool _remise;
    private IDisposable? _abonnement;

    public SelfUpdateHostedService(
        SelfUpdateService update,
        IOptionsMonitor<ApiExposeOptions> options,
        ILogger<SelfUpdateHostedService> logger,
        IEventBus bus)
    {
        _update = update;
        _options = options;
        _logger = logger;
        _bus = bus;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var opt = _options.CurrentValue.SelfUpdate;
        if (!opt.Enabled) return Task.CompletedTask;
        _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement);
        _ = Task.Run(() => BoucleAsync(_arret.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// UNE MISE A JOUR REMISE PARCE QU'UN JEU TOURNAIT REPART A LA FERMETURE DU JEU (demande user,
    /// 2026-10-16), 30 s apres, le temps de voir si un autre jeu se lance ; a defaut d'en entendre la
    /// fin, au bout de 10 minutes. Elle attendait la verification suivante, six heures plus tard.
    /// </summary>
    internal static readonly TimeSpan RelanceSiOccupee = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan ApresLaFinDuJeu = TimeSpan.FromSeconds(30);

    private void SurEvenement(EventEnvelope evenement)
    {
        if (!_remise || !string.Equals(evenement.Type, "ui.game.ended", StringComparison.Ordinal)) return;
        if (_finDeJeu.CurrentCount == 0)
        {
            try { _finDeJeu.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private async Task BoucleAsync(CancellationToken ct)
    {
        var opt = _options.CurrentValue.SelfUpdate;
        // LES OUTILS TOUT DE SUITE (demande user, 2026-10-16) : juste apres une mise a jour, l'API qui
        // demarre prend les outils de sa version (l'exe du direct, l'outil de diagnostic) sans attendre
        // la verification de la mise a jour suivante.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            var etat = await _update.VerifierAsync(ct).ConfigureAwait(false);
            if (!etat.UpdateAvailable && etat.ToolsManifestUrl is { Length: > 0 } outils)
            {
                await _update.MettreLesOutilsAJourAsync(outils, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Outils : verification impossible au demarrage.");
        }

        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, opt.StartupDelaySeconds)), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            opt = _options.CurrentValue.SelfUpdate;
            var remise = false;
            _remise = false;
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
                else if (etat.UpdateAvailable && etat.Busy.Length > 0)
                {
                    remise = true;   // un jeu tournait : on repasse a sa fermeture
                    _remise = true;
                }
                else if (!etat.UpdateAvailable)
                {
                    _logger.LogInformation("APIExpose {Version} : deja a jour.", etat.Installed);
                    // L'outil de diagnostic voyage hors de l'update.7z : on le prend a part, une fois
                    // la version de l'API a jour (sinon c'est la mise a jour qui passe d'abord).
                    if (etat.ToolsManifestUrl is { Length: > 0 } outils)
                    {
                        await _update.MettreLesOutilsAJourAsync(outils, ct).ConfigureAwait(false);
                    }
                    if (opt.IntervalHours <= 0) return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Mise a jour : verification impossible.");
            }

            // Rendez-vous plus tard : a la fermeture du jeu (ou dans 10 minutes) s'il tournait, sinon a la
            // verification suivante.
            var heures = opt.IntervalHours <= 0 ? 6 : opt.IntervalHours;
            try
            {
                if (remise)
                {
                    var reveil = await _finDeJeu.WaitAsync(RelanceSiOccupee, ct).ConfigureAwait(false);
                    if (reveil)
                    {
                        _logger.LogInformation("Mise a jour : le jeu est ferme, nouvelle tentative dans {Secondes} s.", ApresLaFinDuJeu.TotalSeconds);
                        await Task.Delay(ApresLaFinDuJeu, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    await Task.Delay(TimeSpan.FromHours(heures), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { return; }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _abonnement?.Dispose();
        _arret.Cancel();
        return Task.CompletedTask;
    }
}
