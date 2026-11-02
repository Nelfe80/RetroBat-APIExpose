using System.Diagnostics;
using System.Runtime.Versioning;
using RetroBat.Api.Infrastructure;

namespace RetroBat.Api.Speedrun;

/// <summary>Les priorites de la borne pendant un defi : le jeu d'abord.</summary>
public interface IPrioritesDuJeu
{
    /// <summary>Le defi s'ouvre : RetroArch passe devant, ES s'efface. RetroArch est attendu s'il n'a pas encore demarre.</summary>
    Task DonnerLaMainAuJeuAsync(CancellationToken ct);

    /// <summary>Le defi se ferme, ou l'API demarre : ES retrouve sa priorite normale.</summary>
    void RendreLaMain();
}

/// <summary>
/// LE JEU D'ABORD. Mesure du 01/11 sur la borne de reference (Core i3-N305 charge a 97 %), trois alternances de 15 s sur
/// Sonic lance par le defi : 41 a 43 images par seconde aux priorites normales, 52 a 55 avec RetroArch au-dessus de la
/// normale et ES en dessous. Lance par l'API, un defi laisse ES dessiner son menu derriere le jeu (20 a 30 % d'un coeur),
/// alors qu'ES se met de lui-meme en veille quand il lance le jeu. Au-dessus de la normale seulement : « haute » ou
/// « temps reel » priveraient le pilote d'affichage et l'API, qui lit les evenements du jeu.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PrioritesDuJeuWindows(ILogger<PrioritesDuJeuWindows> journal) : IPrioritesDuJeu
{
    /// <summary>emulatorLauncher prepare la partie avant de demarrer RetroArch : jusqu'a une minute sur une borne chargee.</summary>
    private static readonly TimeSpan AttenteDeRetroArch = TimeSpan.FromSeconds(60);

    public async Task DonnerLaMainAuJeuAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        Poser("emulationstation", ProcessPriorityClass.BelowNormal);
        var limite = DateTime.UtcNow + AttenteDeRetroArch;
        while (!ct.IsCancellationRequested && DateTime.UtcNow < limite)
        {
            if (Poser("retroarch", ProcessPriorityClass.AboveNormal) > 0) return;
            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        if (!ct.IsCancellationRequested) journal.LogInformation("Speedrun : RetroArch introuvable apres une minute, sa priorite reste normale.");
    }

    public void RendreLaMain() => Poser("emulationstation", ProcessPriorityClass.Normal);

    /// <summary>Pose la priorite sur chaque processus du prefixe ; rend le nombre de processus a cette priorite.</summary>
    private int Poser(string prefixe, ProcessPriorityClass priorite)
    {
        var poses = 0;
        foreach (var p in EmulatorForeground.ProcessusCommencantPar(prefixe))
        {
            using (p)
            {
                try
                {
                    if (p.PriorityClass != priorite)
                    {
                        p.PriorityClass = priorite;
                        journal.LogInformation("Speedrun : priorite de {Processus} ({Pid}) : {Priorite}.", p.ProcessName, p.Id, priorite);
                    }

                    poses++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    journal.LogDebug(ex, "Speedrun : priorite de {Prefixe} non posee.", prefixe);
                }
            }
        }

        return poses;
    }
}
