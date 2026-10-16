using System.Diagnostics;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>Les chemins de RetroArch NelfePlay, relatifs au dossier d'APIExpose.</summary>
public static class RetroArchNelfePlay
{
    /// <summary>L'archive officielle de la nocturne, livree par outils.json.</summary>
    public const string CheminDeLArchive = "tools/retroarch-nelfeplay/RetroArch_update-2026-10-16.7z";

    public const string Installeur = "install-retroarch-nelfeplay.bat";

    /// <summary>Pose par le desinstalleur : le joueur est revenu a la version de RetroBat, on ne reinstalle pas.</summary>
    public const string Refus = "state/retroarch-nelfeplay-refus.txt";
}

/// <summary>
/// RETROARCH NELFEPLAY S'INSTALLE TOUT SEUL (decision du user, 2026-10-16) : la mise a jour livre l'archive de la nocturne
/// corrigee (outils.json) et ce service lance install-retroarch-nelfeplay.bat des qu'aucun jeu ne tourne. RetroArch 1.22.2
/// plante en ouvrant un replay sur un gros etat (TGM2, MAME libretro) ; la nocturne non.
///
/// Le retour en arriere reste au joueur : uninstall-retroarch-nelfeplay.bat remet le RetroArch de RetroBat et pose un
/// refus, que ce service respecte ; relancer l'installeur a la main le retire. Une mise a jour de RetroBat qui remet sa
/// 1.22.2 (la marque reste, l'empreinte de l'exe non) se rattrape au tour suivant.
/// </summary>
public sealed class RetroArchNelfePlayService : BackgroundService
{
    private static readonly TimeSpan PremierEssai = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Releve = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ApresUnEchec = TimeSpan.FromHours(1);
    private static readonly string[] Emulateurs = ["retroarch", "emulatorLauncher", "mame", "mame64"];

    private readonly IEventBus _bus;
    private readonly ApiContext _contexte;
    private readonly ILogger<RetroArchNelfePlayService> _journal;
    private readonly bool _actif;
    private IDisposable? _abonnement;
    private int _enCours;
    private DateTime _pasAvant = DateTime.MinValue;
    private bool _refusDit;

    public RetroArchNelfePlayService(IEventBus bus, ApiContext contexte, IConfiguration configuration, ILogger<RetroArchNelfePlayService> journal)
    {
        _bus = bus;
        _contexte = contexte;
        _journal = journal;
        _actif = configuration.GetValue("RetroArchNelfePlay:AutoInstall", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_actif) return;
        try { _abonnement = _bus.Subscribe<EventEnvelope>(SurEvenement); }
        catch (Exception ex) { _journal.LogDebug(ex, "RetroArch NelfePlay : abonnement au bus impossible."); }
        try
        {
            await Task.Delay(PremierEssai, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await EssayerAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(Releve, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _abonnement?.Dispose();
        }
    }

    private void SurEvenement(EventEnvelope e)
    {
        // La fin d'une partie libere RetroArch : le moment d'installer, une fois ES revenu.
        if (string.Equals(e.Type, "ui.game.ended", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                await EssayerAsync(CancellationToken.None).ConfigureAwait(false);
            });
        }
    }

    /// <summary>Faut-il installer ? Tout est la, rien n'est refuse ni deja fait, et rien ne tourne. Pur.</summary>
    internal static bool AInstaller(bool archive, bool installeur, bool refus, bool dejaInstallee, bool unJeuTourne)
        => archive && installeur && !refus && !dejaInstallee && !unJeuTourne;

    private async Task EssayerAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _enCours, 1) == 1) return;
        try
        {
            if (DateTime.UtcNow < _pasAvant) return;
            var racine = RetroBatPaths.PluginRoot;
            var bat = Path.Combine(racine, RetroArchNelfePlay.Installeur);
            var refus = File.Exists(Path.Combine(racine, RetroArchNelfePlay.Refus));
            if (refus && !_refusDit)
            {
                _refusDit = true;
                _journal.LogInformation("RetroArch NelfePlay : desinstalle par le joueur, pas de reinstallation (relancer {Bat} pour la reprendre).",
                    RetroArchNelfePlay.Installeur);
            }
            var unJeuTourne = _contexte.Ui.Running is not null
                || Process.GetProcesses().Any(p => Emulateurs.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase));
            if (!AInstaller(File.Exists(Path.Combine(racine, RetroArchNelfePlay.CheminDeLArchive)), File.Exists(bat), refus,
                    RetroBat.Api.Replay.Recording.ReplayRecorderService.RetroArchNelfePlayInstalle(), unJeuTourne))
            {
                return;
            }

            _journal.LogInformation("RetroArch NelfePlay : installation de la nocturne corrigee (aucun jeu ne tourne).");
            using var processus = new Process
            {
                StartInfo = new ProcessStartInfo("cmd.exe", "/c \"\"" + bat + "\" /silencieux\"")
                {
                    WorkingDirectory = racine,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            processus.Start();
            var sortie = processus.StandardOutput.ReadToEndAsync(ct);
            using (var delai = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                delai.CancelAfter(TimeSpan.FromMinutes(10));
                try { await processus.WaitForExitAsync(delai.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { processus.Kill(true); } catch { }
                }
            }
            var texte = string.Join(" | ", (await sortie.ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (processus.HasExited && processus.ExitCode == 0 && RetroBat.Api.Replay.Recording.ReplayRecorderService.RetroArchNelfePlayInstalle())
            {
                _journal.LogInformation("RetroArch NelfePlay : installee ({Sortie}).", texte);
            }
            else
            {
                _pasAvant = DateTime.UtcNow + ApresUnEchec;
                _journal.LogWarning("RetroArch NelfePlay : installation non faite (code {Code}) : {Sortie}. Nouvel essai dans une heure.",
                    processus.HasExited ? processus.ExitCode : -1, texte);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _pasAvant = DateTime.UtcNow + ApresUnEchec;
            _journal.LogWarning("RetroArch NelfePlay : installation impossible ({Erreur}). Nouvel essai dans une heure.", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _enCours, 0);
        }
    }
}
