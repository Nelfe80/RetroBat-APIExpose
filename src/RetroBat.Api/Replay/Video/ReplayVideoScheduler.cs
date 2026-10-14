using System.Runtime.InteropServices;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Playback;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LE RENDU DES FILMS AU REPOS (CDC video V2, lot 1). La borne filme ses records quand personne ne s'en sert :
/// aucune partie, aucune lecture, aucun emulateur, ni navigation, ni appui sur le panel, ni clavier ou souris
/// depuis un moment. Le joueur passe toujours avant : la moindre activite arrete le rendu en cours et rend la
/// machine. Le site decide quoi filmer et combien par jour ; la borne ne rend qu'un film par passage, et
/// repasse moins souvent quand il n'y a rien a filmer.
/// </summary>
public sealed class ReplayVideoScheduler : BackgroundService
{
    private readonly IEventBus _bus;
    private readonly ReplayVideoPublisher _publication;
    private readonly ReplayPlaybackService _playback;
    private readonly ILogger<ReplayVideoScheduler> _logger;
    private readonly bool _actif;
    private readonly TimeSpan _repos;
    private readonly TimeSpan _intervalle;
    private readonly TimeSpan _rienAFilmer;

    private readonly object _verrou = new();
    private DateTime _derniereActivite = DateTime.UtcNow;
    private bool _partieEnCours;
    private CancellationTokenSource? _renduEnCours;
    private IDisposable? _abonnement;

    public ReplayVideoScheduler(IEventBus bus, ReplayVideoPublisher publication, ReplayPlaybackService playback,
        IConfiguration configuration, ILogger<ReplayVideoScheduler> logger)
    {
        _bus = bus; _publication = publication; _playback = playback; _logger = logger;
        _actif = configuration.GetValue("Video:Render:Enabled", true);
        _repos = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Video:Render:IdleMinutes", 15)));
        _intervalle = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Video:Render:IntervalMinutes", 5)));
        _rienAFilmer = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Video:Render:NothingToDoMinutes", 60)));
    }

    /// <summary>La borne est-elle au repos ? Pur, pour etre teste.</summary>
    internal static bool AuRepos(DateTime maintenant, DateTime derniereActivite, TimeSpan repos, bool partieEnCours,
        bool emulateurEnCours, bool lectureEnCours)
        => !partieEnCours && !emulateurEnCours && !lectureEnCours && maintenant - derniereActivite >= repos;

    /// <summary>
    /// La derniere activite connue : celle de la borne (evenements d'ES et du panel) ou celle du clavier et de
    /// la souris, la plus recente des deux. Pur, pour etre teste.
    /// </summary>
    internal static DateTime DerniereActivite(DateTime maintenant, DateTime activiteDeLaBorne, TimeSpan? inactiviteWindows)
        => inactiviteWindows is { } duree && maintenant - duree > activiteDeLaBorne ? maintenant - duree : activiteDeLaBorne;

    /// <summary>
    /// Le delai avant le prochain passage : plus long quand rien n'etait filmable, ou quand l'envoi a ete refuse
    /// (le quota du jour ne revient pas en cinq minutes, et chaque rendu perdu prend la machine). Pur, pour etre
    /// teste.
    /// </summary>
    internal static TimeSpan ProchainPassage(ReplayVideoPublisher.Rapport? rapport, TimeSpan intervalle, TimeSpan rienAFilmer)
        => rapport is { Rendus: 0 } or { EnvoiRefuse: true } ? rienAFilmer : intervalle;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_actif)
        {
            _logger.LogInformation("Video : rendu au repos desactive (Video:Render:Enabled).");
            return;
        }
        try { _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement); }
        catch (Exception ex) { _logger.LogWarning(ex, "Video : bus d'evenements indisponible, rendu au repos coupe."); return; }
        _logger.LogInformation("Video : rendu au repos actif (apres {Repos} min sans activite, passage toutes les {Intervalle} min).",
            _repos.TotalMinutes, _intervalle.TotalMinutes);

        var attente = _intervalle;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(attente, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            try
            {
                attente = await PasserAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // L'hote s'arrete sur toute exception d'un service de fond (StopHost) : un passage rate ne doit
                // jamais emporter l'API.
                _logger.LogWarning(ex, "Video : passage au repos en echec.");
                attente = _intervalle;
            }
        }
    }

    /// <summary>Un passage : filme un record si la borne est au repos. Rend le delai avant le suivant.</summary>
    private async Task<TimeSpan> PasserAsync(CancellationToken stoppingToken)
    {
        bool partie; DateTime activiteDeLaBorne;
        lock (_verrou) { partie = _partieEnCours; activiteDeLaBorne = _derniereActivite; }
        var maintenant = DateTime.UtcNow;
        var activite = DerniereActivite(maintenant, activiteDeLaBorne, InactiviteWindows());
        if (!AuRepos(maintenant, activite, _repos, partie, EmulatorForeground.EmulateurTourne(),
                _playback.GetState().State != "idle"))
            return _intervalle;

        using var rendu = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_verrou)
        {
            // Une activite arrivee entre-temps : on ne commence pas.
            if (_partieEnCours || _derniereActivite != activiteDeLaBorne) return _intervalle;
            _renduEnCours = rendu;
        }
        var veille = VeillerPendantLeRenduAsync(maintenant, rendu);
        try
        {
            var rapport = await _publication.PublierAsync(1, rendu.Token).ConfigureAwait(false);
            return ProchainPassage(rapport, _intervalle, _rienAFilmer);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Video : rendu interrompu, la borne reprend du service.");
            return _intervalle;
        }
        finally
        {
            lock (_verrou) _renduEnCours = null;
            try { rendu.Cancel(); } catch (Exception) { /* deja libere */ }
            await veille.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Le clavier ou la souris servent pendant le rendu : on l'arrete. Les evenements d'ES et du panel le font
    /// deja, directement (<see cref="SurEvenement"/>).
    /// </summary>
    private static async Task VeillerPendantLeRenduAsync(DateTime debut, CancellationTokenSource rendu)
    {
        try
        {
            while (!rendu.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), rendu.Token).ConfigureAwait(false);
                if (InactiviteWindows() is { } duree && DateTime.UtcNow - duree > debut)
                    rendu.Cancel();
            }
        }
        catch (Exception) { /* fin du rendu, ou annulation deja faite : jamais d'exception d'ici */ }
    }

    private void SurEvenement(EventEnvelope e)
    {
        var activite = true;
        lock (_verrou)
        {
            switch (e.Type)
            {
                case "ui.game.started": _partieEnCours = true; break;
                case "ui.game.ended": _partieEnCours = false; break;
                case "ui.game.selected":
                case "ui.system.selected":
                case "panel.input.pressed":
                    break;
                default:
                    activite = false;
                    break;
            }
            if (!activite) return;
            _derniereActivite = DateTime.UtcNow;
            // Le joueur passe avant : le rendu s'arrete et rend la machine.
            try { _renduEnCours?.Cancel(); } catch (Exception) { /* deja termine */ }
        }
    }

    /// <summary>
    /// Depuis quand ni le clavier ni la souris n'ont servi, selon Windows (les manettes n'y comptent pas : le
    /// panel passe par ses propres evenements). Null si Windows ne repond pas.
    /// </summary>
    private static TimeSpan? InactiviteWindows()
    {
        try
        {
            var info = new DerniereSaisie { Taille = (uint)Marshal.SizeOf<DerniereSaisie>() };
            if (!GetLastInputInfo(ref info)) return null;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Instant));
        }
        catch (Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DerniereSaisie
    {
        public uint Taille;
        public uint Instant;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref DerniereSaisie info);

    public override void Dispose()
    {
        _abonnement?.Dispose();
        base.Dispose();
    }
}
