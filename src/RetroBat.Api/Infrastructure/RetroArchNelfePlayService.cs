using System.Diagnostics;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>Les chemins de RetroArch NelfePlay, relatifs au dossier d'APIExpose.</summary>
public static class RetroArchNelfePlay
{
    /// <summary>L'archive officielle de la nocturne (livree par outils.json en 1.9.60 seulement).</summary>
    public const string CheminDeLArchive = "tools/retroarch-nelfeplay/RetroArch_update-2026-10-16.7z";

    public const string Installeur = "install-retroarch-nelfeplay.bat";

    public const string Desinstalleur = "uninstall-retroarch-nelfeplay.bat";

    /// <summary>La marque de l'installeur, dans le dossier de RetroArch : la nocturne a ete posee ici.</summary>
    public const string Marque = ".nelfeplay/installe.txt";

    /// <summary>Pose par le desinstalleur : le joueur est revenu a la version de RetroBat, on ne reinstalle pas.</summary>
    public const string Refus = "state/retroarch-nelfeplay-refus.txt";
}

/// <summary>
/// RETROARCH NELFEPLAY SE RETIRE (2026-10-17). La 1.9.60 installait d'elle-meme la nocturne de RetroArch du 16 octobre, qui
/// tient le replay des gros etats (TGM2, MAME libretro). Mais elle lit les manettes par SDL3, quand RetroBat ecrit a chaque
/// lancement des touches numerotees pour SDL2 : chez les joueurs, la piece sur L1, START sur L2, la croix morte ; on ne
/// pouvait plus jouer, et aucune partie ne marquait de points (theJim, Gogerad, la nuit du 16 au 17).
///
/// Ce service la DESINSTALLE donc, la ou elle est posee, des qu'aucun jeu ne tourne (uninstall-retroarch-nelfeplay.bat
/// /annulation : RetroArch de RetroBat remis tel qu'il etait, sans poser de refus). L'installation automatique reste
/// possible (RetroArchNelfePlay:AutoInstall), coupee par defaut. Sous la 1.22.2, TGM2 et MAME libretro restent sans replay.
/// </summary>
public sealed class RetroArchNelfePlayService : BackgroundService
{
    private static readonly TimeSpan PremierEssai = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Releve = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ApresUnEchec = TimeSpan.FromMinutes(30);
    private static readonly string[] Emulateurs = ["retroarch", "emulatorLauncher", "mame", "mame64"];

    private readonly IEventBus _bus;
    private readonly ApiContext _contexte;
    private readonly ILogger<RetroArchNelfePlayService> _journal;
    private readonly bool _installer;
    private readonly bool _retirer;
    private IDisposable? _abonnement;
    private int _enCours;
    private DateTime _pasAvant = DateTime.MinValue;

    public RetroArchNelfePlayService(IEventBus bus, ApiContext contexte, IConfiguration configuration, ILogger<RetroArchNelfePlayService> journal)
    {
        _bus = bus;
        _contexte = contexte;
        _journal = journal;
        _installer = configuration.GetValue("RetroArchNelfePlay:AutoInstall", false);
        _retirer = !_installer && configuration.GetValue("RetroArchNelfePlay:Retrait", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_installer && !_retirer) return;
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
        // La fin d'une partie libere RetroArch : le moment d'agir, une fois ES revenu.
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

    /// <summary>Faut-il retirer la nocturne ? Elle est posee (la marque), le desinstalleur est la, et rien ne tourne. Pur.</summary>
    internal static bool ARetirer(bool marque, bool desinstalleur, bool unJeuTourne) => marque && desinstalleur && !unJeuTourne;

    private async Task EssayerAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _enCours, 1) == 1) return;
        try
        {
            if (DateTime.UtcNow < _pasAvant) return;
            var racine = RetroBatPaths.PluginRoot;
            var dossierRetroArch = Path.GetDirectoryName(RetroBatPaths.RetroArchConfigPath) ?? "";
            var unJeuTourne = _contexte.Ui.Running is not null
                || Process.GetProcesses().Any(p => Emulateurs.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase));

            if (_retirer)
            {
                var desinstalleur = Path.Combine(racine, RetroArchNelfePlay.Desinstalleur);
                if (!ARetirer(File.Exists(Path.Combine(dossierRetroArch, RetroArchNelfePlay.Marque)), File.Exists(desinstalleur), unJeuTourne)) return;
                _journal.LogInformation("RetroArch NelfePlay : retrait de la nocturne (ses manettes SDL3 ne suivent pas les touches de RetroBat).");
                var (code, texte) = await LancerAsync(desinstalleur, "/silencieux /annulation", racine, ct).ConfigureAwait(false);
                if (code == 0 && !File.Exists(Path.Combine(dossierRetroArch, RetroArchNelfePlay.Marque)))
                {
                    _journal.LogInformation("RetroArch NelfePlay : retiree, RetroArch de RetroBat remis ({Sortie}).", texte);
                }
                else
                {
                    _pasAvant = DateTime.UtcNow + ApresUnEchec;
                    _journal.LogWarning("RetroArch NelfePlay : retrait non fait (code {Code}) : {Sortie}. Nouvel essai dans 30 min.", code, texte);
                }
                return;
            }

            var installeur = Path.Combine(racine, RetroArchNelfePlay.Installeur);
            var refus = File.Exists(Path.Combine(racine, RetroArchNelfePlay.Refus));
            if (!AInstaller(File.Exists(Path.Combine(racine, RetroArchNelfePlay.CheminDeLArchive)), File.Exists(installeur), refus,
                    RetroBat.Api.Replay.Recording.ReplayRecorderService.RetroArchNelfePlayInstalle(), unJeuTourne))
            {
                return;
            }
            _journal.LogInformation("RetroArch NelfePlay : installation de la nocturne corrigee (aucun jeu ne tourne).");
            var (codeInstall, sortie) = await LancerAsync(installeur, "/silencieux", racine, ct).ConfigureAwait(false);
            if (codeInstall == 0 && RetroBat.Api.Replay.Recording.ReplayRecorderService.RetroArchNelfePlayInstalle())
            {
                _journal.LogInformation("RetroArch NelfePlay : installee ({Sortie}).", sortie);
            }
            else
            {
                _pasAvant = DateTime.UtcNow + ApresUnEchec;
                _journal.LogWarning("RetroArch NelfePlay : installation non faite (code {Code}) : {Sortie}.", codeInstall, sortie);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _pasAvant = DateTime.UtcNow + ApresUnEchec;
            _journal.LogWarning("RetroArch NelfePlay : operation impossible ({Erreur}).", ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _enCours, 0);
        }
    }

    /// <summary>Lance un des deux scripts, sans fenetre, sa sortie gardee pour le journal ; dix minutes au plus.</summary>
    private static async Task<(int Code, string Sortie)> LancerAsync(string bat, string arguments, string dossier, CancellationToken ct)
    {
        using var processus = new Process
        {
            StartInfo = new ProcessStartInfo("cmd.exe", "/c \"\"" + bat + "\" " + arguments + "\"")
            {
                WorkingDirectory = dossier,
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
        return (processus.HasExited ? processus.ExitCode : -1, texte);
    }
}
