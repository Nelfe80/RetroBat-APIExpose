using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Ramener le jeu DEVANT, quand c'est le web qui l'a demandé.
///
/// Un lancement déclenché depuis un navigateur part avec un handicap : la fenêtre au premier
/// plan est celle du navigateur, et Windows n'accorde pas volontiers le premier plan à un
/// processus qui n'a pas le focus. EmulationStation démarre alors l'émulateur DERRIÈRE le
/// navigateur, et le joueur voit sa page au lieu de son jeu.
///
/// Deux temps, donc, et ils comptent tous les deux : EmulationStation reçoit le focus AVANT
/// le lancement — sans quoi l'émulateur naît en arrière-plan — puis l'émulateur le reçoit
/// dès que sa fenêtre existe. La fenêtre n'existe pas tout de suite : un cœur qui charge une
/// ROM d'arcade prend quelques secondes, d'où la surveillance plutôt qu'un unique essai.
///
/// Ce code vivait en trois exemplaires (annonce de défi, contest, entrées ES). Les mêmes
/// P/Invoke recopiés finissent par ne plus se ressembler.
/// </summary>
[SupportedOSPlatform("windows")]
public static class EmulatorForeground
{
    private const int SwRestore = 9;

    /// <summary>Les émulateurs que RetroBat lance. Le premier trouvé avec une fenêtre gagne.</summary>
    private static readonly string[] Emulateurs =
        ["retroarch", "mame", "mame64", "fbneo", "pcsx2", "dolphin", "duckstation-qt-x64", "ppsspp"];

    /// <summary>Le menu. Il doit avoir le focus avant qu'on lui demande de lancer quoi que ce soit.</summary>
    public static void FocusEmulationStation() => Focus("emulationstation");

    /// <summary>Ramène au premier plan toutes les fenêtres de ce processus, s'il y en a.</summary>
    public static bool Focus(string processName)
    {
        var trouve = false;
        try
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                if (process.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }
                // SW_RESTORE d'abord : une fenêtre réduite ne peut pas passer au premier plan.
                ShowWindow(process.MainWindowHandle, SwRestore);
                SetForegroundWindow(process.MainWindowHandle);
                trouve = true;
            }
        }
        catch (Exception)
        {
            // Un émulateur qui refuse le premier plan ne doit jamais faire échouer une partie.
        }
        return trouve;
    }

    /// <summary>
    /// Surveille l'apparition d'un émulateur et le ramène devant. À lancer sans l'attendre :
    /// la réponse HTTP ne doit pas patienter le temps qu'un cœur charge sa ROM.
    /// </summary>
    public static async Task FocusEmulatorWhenUpAsync(TimeSpan? fenetre = null, CancellationToken ct = default)
    {
        var limite = DateTime.UtcNow + (fenetre ?? TimeSpan.FromSeconds(60));
        while (DateTime.UtcNow < limite && !ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(700), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            foreach (var nom in Emulateurs)
            {
                if (Focus(nom))
                {
                    // Un deuxième passage, une seconde plus tard : l'émulateur ouvre parfois
                    // sa fenêtre puis la recrée en plein écran, ce qui rend le premier plan.
                    try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                    Focus(nom);
                    return;
                }
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
