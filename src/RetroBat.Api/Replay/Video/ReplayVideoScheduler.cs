using System.Runtime.InteropServices;
using RetroBat.Api.Infrastructure;
using RetroBat.Api.Replay.Playback;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// QUAND LA BORNE FILME (CDC video V2, lot 1). Le site donne son role a la borne (decision du 14 octobre 2026) :
/// <list type="bullet">
/// <item>une STATION DE RENDU filme a toute heure, que quelqu'un se serve du PC ou non, tant qu'aucune partie,
/// lecture ni emulateur ne tourne ; seule une partie qui commence arrete le rendu ;</item>
/// <item>la borne d'un JOUEUR s'eteint souvent des la partie finie : elle ne filme rien, sauf si le site la
/// laisse filmer ses propres records, et alors seulement au repos (ni navigation, ni panel, ni clavier ou
/// souris depuis un moment), la moindre activite arretant le rendu ;</item>
/// <item>sans role, la borne redemande de temps en temps, sans rien filmer.</item>
/// </list>
/// Le site decide quoi filmer et combien par jour ; la borne ne rend qu'un film par passage, et repasse moins
/// souvent quand il n'y a rien a filmer.
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
    private readonly TimeSpan _sansRole;

    private readonly object _verrou = new();
    private DateTime _derniereActivite = DateTime.UtcNow;
    private bool _partieEnCours;
    private CancellationTokenSource? _renduEnCours;
    private IDisposable? _abonnement;
    // Le dernier role donne par le site ; null avant sa premiere reponse.
    private string? _role;

    public ReplayVideoScheduler(IEventBus bus, ReplayVideoPublisher publication, ReplayPlaybackService playback,
        IConfiguration configuration, ILogger<ReplayVideoScheduler> logger)
    {
        _bus = bus; _publication = publication; _playback = playback; _logger = logger;
        _actif = configuration.GetValue("Video:Render:Enabled", true);
        _repos = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Video:Render:IdleMinutes", 15)));
        _intervalle = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Video:Render:IntervalMinutes", 5)));
        _rienAFilmer = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Video:Render:NothingToDoMinutes", 60)));
        _sansRole = TimeSpan.FromHours(Math.Max(1, configuration.GetValue("Video:Render:NoRoleHours", 6)));
    }

    /// <summary>
    /// La borne peut-elle filmer maintenant ? Une station des qu'aucune partie, lecture ni emulateur ne tourne ;
    /// une borne de joueur seulement au repos. Pur, pour etre teste.
    /// </summary>
    internal static bool PeutFilmer(string? role, DateTime maintenant, DateTime derniereActivite, TimeSpan repos,
        bool partieEnCours, bool emulateurEnCours, bool lectureEnCours)
        => role == ReplayVideoPublisher.RoleStation
            ? !partieEnCours && !emulateurEnCours && !lectureEnCours
            : AuRepos(maintenant, derniereActivite, repos, partieEnCours, emulateurEnCours, lectureEnCours);

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
        if (RetroBat.Api.Infrastructure.EmulatorForeground.ArreterLeRenduOrphelin())
        {
            _logger.LogWarning("Video : un rendu reste d'une API arretee, il n'a plus de pilote : arrete.");
        }

        if (!_actif)
        {
            _logger.LogInformation("Video : rendu au repos desactive (Video:Render:Enabled).");
            return;
        }
        try { _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement); }
        catch (Exception ex) { _logger.LogWarning(ex, "Video : bus d'evenements indisponible, rendu au repos coupe."); return; }
        _logger.LogInformation("Video : rendu des films actif (passage toutes les {Intervalle} min ; borne de joueur : apres {Repos} min sans activite).",
            _intervalle.TotalMinutes, _repos.TotalMinutes);

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

    /// <summary>Un passage : filme un record si le role et l'etat de la borne le permettent. Rend le delai avant le suivant.</summary>
    private async Task<TimeSpan> PasserAsync(CancellationToken stoppingToken)
    {
        bool partie; DateTime activiteDeLaBorne; string? role;
        lock (_verrou) { partie = _partieEnCours; activiteDeLaBorne = _derniereActivite; role = _role; }
        var maintenant = DateTime.UtcNow;
        var activite = DerniereActivite(maintenant, activiteDeLaBorne, InactiviteWindows());
        var emulateur = EmulatorForeground.EmulateurTourne();
        var lecture = _playback.GetState().State != "idle";
        // Role encore inconnu : on le demande des que la borne est libre, sans attendre le repos.
        var libre = role is null ? !partie && !emulateur && !lecture
            : PeutFilmer(role, maintenant, activite, _repos, partie, emulateur, lecture);
        if (!libre) return _intervalle;

        var liste = await _publication.ListerAsync(stoppingToken).ConfigureAwait(false);
        if (liste.Role is null) return _intervalle;
        if (liste.Role != role)
            _logger.LogInformation("Video : role de cette borne pour les films : {Role}.", liste.Role);
        lock (_verrou) _role = liste.Role;
        if (liste.Role == ReplayVideoPublisher.RoleAucun) return _sansRole;
        if (!PeutFilmer(liste.Role, maintenant, activite, _repos, partie, emulateur, lecture)) return _intervalle;
        if (liste.Cibles.Count == 0) return _rienAFilmer;

        var station = liste.Role == ReplayVideoPublisher.RoleStation;
        using var rendu = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_verrou)
        {
            // Une partie, ou chez un joueur une activite, arrivee entre-temps : on ne commence pas.
            if (_partieEnCours || (!station && _derniereActivite != activiteDeLaBorne)) return _intervalle;
            _renduEnCours = rendu;
        }
        var veille = station ? Task.CompletedTask : VeillerPendantLeRenduAsync(maintenant, rendu);
        try
        {
            var rapport = await _publication.FilmerAsync(liste, 1, rendu.Token).ConfigureAwait(false);
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
            // Le joueur passe avant : le rendu s'arrete et rend la machine. Une station ne s'arrete que pour
            // une partie.
            if (e.Type == "ui.game.started" || _role != ReplayVideoPublisher.RoleStation)
            {
                try { _renduEnCours?.Cancel(); } catch (Exception) { /* deja termine */ }
            }
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
