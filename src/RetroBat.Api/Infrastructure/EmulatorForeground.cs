using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Ramener le jeu DEVANT, quand c'est le web qui l'a demandé.
///
/// Un lancement déclenché depuis un navigateur part avec un handicap, et ce handicap est une
/// protection de Windows : un processus qui ne possède pas la fenêtre de premier plan n'a pas
/// le droit de la lui prendre. <c>SetForegroundWindow</c> échoue alors SANS erreur — il fait
/// clignoter la barre des tâches et rien de plus. APIExpose étant un service d'arrière-plan et
/// le navigateur ayant le focus, l'émulateur naît derrière lui.
///
/// La parade documentée : s'ATTACHER à la file d'entrée du fil qui possède le premier plan.
/// Les deux fils partagent alors leur état d'entrée, et Windows accorde le changement. En
/// dernier recours, un passage éclair par « toujours au-dessus » remonte la fenêtre dans la
/// pile sans lui laisser ce rang.
///
/// Deux temps, et ils comptent tous les deux : EmulationStation reçoit le focus AVANT le
/// lancement — sinon l'émulateur naît en arrière-plan — puis l'émulateur dès que sa fenêtre
/// existe. Elle n'existe pas tout de suite (un cœur qui charge une ROM d'arcade prend quelques
/// secondes) et elle est parfois RECRÉÉE au passage en plein écran : on ne s'arrête donc pas au
/// premier succès, on vérifie qu'elle est réellement devant et on réaffirme un moment.
///
/// Ce code vivait en trois exemplaires (annonce de défi, contest, entrées ES). Les mêmes
/// P/Invoke recopiés finissent par ne plus se ressembler.
/// </summary>
[SupportedOSPlatform("windows")]
public static class EmulatorForeground
{
    private const int SwRestore = 9;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNoTopmost = new(-2);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>Les émulateurs que RetroBat lance. Le premier trouvé avec une fenêtre gagne.</summary>
    private static readonly string[] Emulateurs =
        ["retroarch", "mame", "mame64", "fbneo", "pcsx2", "dolphin", "duckstation", "ppsspp"];

    /// <summary>
    /// Les processus dont le nom COMMENCE par celui-ci.
    ///
    /// Le prefixe n'est pas une commodite, c'est la correction d'un vrai bug : RetroBat ne lance
    /// pas « retroarch.exe » mais un binaire patche, dont le processus s'appelle
    /// **`retroarch.patched.RETROBAT`** (mesure sur la borne). `GetProcessesByName` compare le
    /// nom ENTIER : il ne trouvait donc rien, et le jeu restait derriere le navigateur — non pas
    /// parce que Windows refusait le premier plan, mais parce qu'on ne cherchait pas la bonne
    /// fenetre.
    /// </summary>
    private static IEnumerable<Process> Processus(string prefixe)
    {
        try
        {
            return Process.GetProcesses()
                .Where(p => p.ProcessName.StartsWith(prefixe, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Le menu. Il doit avoir le focus avant qu'on lui demande de lancer quoi que ce soit.</summary>
    /// <summary>
    /// Un emulateur tourne-t-il ?
    ///
    /// Meme correspondance par PREFIXE que le reste de cette classe, et pour la meme raison :
    /// RetroBat lance « retroarch.patched.RETROBAT », que la recherche par nom entier ne
    /// trouve pas. Poser la question ici evite qu'un appelant reinvente ce piege.
    /// </summary>
    public static bool EmulateurTourne()
    {
        foreach (var nom in Emulateurs)
        {
            var trouves = Processus(nom).ToArray();
            try
            {
                if (trouves.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var p in trouves)
                {
                    p.Dispose();
                }
            }
        }
        return false;
    }

    public static bool FocusEmulationStation() => Focus("emulationstation");

    /// <summary>
    /// Ramène ce processus au premier plan. Vrai seulement s'il Y EST : une fenêtre trouvée
    /// n'est pas une fenêtre devant, et c'est toute la différence ici.
    /// </summary>
    public static bool Focus(string processName)
    {
        try
        {
            foreach (var process in Processus(processName))
            {
                if (process.MainWindowHandle != IntPtr.Zero && Imposer(process.MainWindowHandle))
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // Un émulateur qui refuse le premier plan ne doit jamais faire échouer une partie.
        }
        return false;
    }

    /// <summary>Vrai si une fenêtre de ce processus existe, qu'elle soit devant ou non.</summary>
    private static bool AUneFenetre(string processName)
    {
        try
        {
            foreach (var process in Processus(processName))
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return true;
                }
            }
        }
        catch (Exception)
        {
        }
        return false;
    }

    /// <summary>
    /// Surveille l'apparition d'un émulateur et le ramène devant. À lancer sans l'attendre :
    /// la réponse HTTP ne doit pas patienter le temps qu'un cœur charge sa ROM.
    ///
    /// On ne s'arrête pas au premier succès : RetroArch ouvre une fenêtre puis la RECRÉE en
    /// plein écran, ce qui rend le premier plan au navigateur. On réaffirme donc quelques
    /// secondes après avoir gagné.
    /// </summary>
    public static async Task FocusEmulatorWhenUpAsync(TimeSpan? fenetre = null, CancellationToken ct = default)
    {
        var limite = DateTime.UtcNow + (fenetre ?? TimeSpan.FromSeconds(90));
        string? vu = null;

        while (DateTime.UtcNow < limite && !ct.IsCancellationRequested)
        {
            if (!await PatienterAsync(TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false))
            {
                return;
            }

            foreach (var nom in Emulateurs)
            {
                if (!AUneFenetre(nom))
                {
                    continue;
                }
                vu = nom;
                break;
            }
            if (vu is not null)
            {
                break;
            }
        }

        if (vu is null)
        {
            return;
        }

        // La fenêtre est là. On insiste pendant une douzaine de secondes : le temps qu'elle
        // passe en plein écran, et qu'un éventuel refus de Windows se laisse convaincre.
        var insistance = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        var gagne = 0;
        while (DateTime.UtcNow < insistance && !ct.IsCancellationRequested)
        {
            if (Focus(vu))
            {
                // Deux confirmations d'affilée : une seule peut précéder la recréation de la
                // fenêtre en plein écran, qui rendrait le premier plan au navigateur.
                if (++gagne >= 2)
                {
                    return;
                }
            }
            else
            {
                gagne = 0;
            }

            if (!await PatienterAsync(TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private static async Task<bool> PatienterAsync(TimeSpan duree, CancellationToken ct)
    {
        try
        {
            await Task.Delay(duree, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Impose le premier plan à une fenêtre, et dit si elle y est vraiment.
    ///
    /// Windows refuse le changement à un processus qui n'a pas déjà le premier plan. On
    /// s'attache donc à la file d'entrée de celui qui l'a : les deux fils partagent alors leur
    /// état d'entrée, et la demande est accordée. Le détachement est en <c>finally</c> —
    /// laisser deux files attachées perturberait le clavier des deux applications.
    /// </summary>
    private static bool Imposer(IntPtr fenetre)
    {
        if (GetForegroundWindow() == fenetre)
        {
            return true;
        }

        var devant = GetForegroundWindow();
        var filDevant = devant == IntPtr.Zero ? 0 : GetWindowThreadProcessId(devant, out _);
        var monFil = GetCurrentThreadId();
        var attache = filDevant != 0 && filDevant != monFil && AttachThreadInput(filDevant, monFil, true);

        try
        {
            if (IsIconic(fenetre))
            {
                ShowWindow(fenetre, SwRestore);   // une fenêtre réduite ne peut pas passer devant
            }
            BringWindowToTop(fenetre);
            SetForegroundWindow(fenetre);

            if (GetForegroundWindow() != fenetre)
            {
                // Dernier recours : un passage ECLAIR par « toujours au-dessus » la remonte
                // dans la pile. On le retire aussitôt : lui laisser ce rang la ferait passer
                // par-dessus tout le reste du système, ce qui n'est pas demandé.
                SetWindowPos(fenetre, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                SetWindowPos(fenetre, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
                SetForegroundWindow(fenetre);
            }
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (attache)
            {
                AttachThreadInput(filDevant, monFil, false);
            }
        }

        return GetForegroundWindow() == fenetre;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
}
