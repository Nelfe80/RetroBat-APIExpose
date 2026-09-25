using System.Diagnostics;

namespace RetroBat.Api.Replay.Storage;

/// <summary>
/// Recompresse le magasin de replays au repos, quand personne ne joue.
///
/// Les joueurs se plaignaient de la place des replays sur leur disque (2026-09-25) : le magasin
/// grossit à chaque START, et un replay de RetroArch 1.22.2 est aux deux tiers des octets nuls. Une
/// fois compressé il tient en 1 à 2 % de sa taille. Les nouveaux objets arrivent bruts (RetroArch
/// les écrit ainsi) et ceux qu'on a matérialisés pour une lecture le redeviennent : ce service les
/// recompresse au passage suivant.
///
/// JAMAIS PENDANT UNE PARTIE. RetroArch lit le brut pendant un replay, et une compression est un
/// balayage disque qui se sentirait en jeu : on attend qu'il soit fermé. Un fichier qu'un pair est
/// en train de télécharger ne se supprime pas sous Windows ; il reste, et sera repris plus tard.
///
/// `Replay:Store:CompressAtRest` (vrai par défaut) coupe le service sans rien défaire : le magasin
/// lit les deux formes.
/// </summary>
public sealed class ReplayCompactionService : BackgroundService
{
    private static readonly TimeSpan DelaiInitial = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Intervalle = TimeSpan.FromMinutes(15);
    private const int ParPassage = 100;

    private readonly ReplayStore _store;
    private readonly IConfiguration _config;
    private readonly ILogger<ReplayCompactionService> _logger;

    public ReplayCompactionService(ReplayStore store, IConfiguration config, ILogger<ReplayCompactionService> logger)
    {
        _store = store;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(DelaiInitial, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PasserAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Replay : passage de compression du magasin interrompu.");
            }

            try { await Task.Delay(Intervalle, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PasserAsync(CancellationToken ct)
    {
        if (!_config.GetValue("Replay:Store:CompressAtRest", true)) return;
        if (RetroArchOuvert()) return;

        var (compresses, retires, avant, apres) = await _store.CompacterAsync(ParPassage, ct).ConfigureAwait(false);
        if (compresses + retires == 0) return;

        _logger.LogInformation(
            "Replay : magasin compresse au repos, {Compresses} objet(s) {Avant} -> {Apres} octets, {Retires} brut(s) de lecture retire(s).",
            compresses, avant, apres, retires);
    }

    private static bool RetroArchOuvert()
    {
        var ps = Process.GetProcessesByName("retroarch");
        try { return ps.Length > 0; }
        finally { foreach (var p in ps) p.Dispose(); }
    }
}
