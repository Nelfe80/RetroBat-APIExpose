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
        ["retroarch", "mame", "mame64", "groovymame", "fbneo", "pcsx2", "dolphin", "duckstation", "ppsspp"];

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
        // Les processus ecartes sont liberes tout de suite : sur une borne, ils sont pres de
        // trois cents a chaque parcours, et chacun tient un handle jusqu'au ramasse-miettes.
        Process[] tous;
        try
        {
            tous = Process.GetProcesses();
        }
        catch (Exception)
        {
            return [];
        }

        var gardes = new List<Process>();
        foreach (var p in tous)
        {
            var garder = false;
            try { garder = p.ProcessName.StartsWith(prefixe, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { }
            if (garder) gardes.Add(p); else p.Dispose();
        }

        return gardes;
    }

    /// <summary>Le menu. Il doit avoir le focus avant qu'on lui demande de lancer quoi que ce soit.</summary>
    /// <summary>
    /// Un emulateur tourne-t-il ?
    ///
    /// Meme correspondance par PREFIXE que le reste de cette classe, et pour la meme raison :
    /// RetroBat lance « retroarch.patched.RETROBAT », que la recherche par nom entier ne
    /// trouve pas. Poser la question ici evite qu'un appelant reinvente ce piege.
    /// </summary>
    /// <summary>
    /// UN SEUL PARCOURS DES PROCESSUS. On en faisait un par nom d'emulateur, soit neuf a chaque
    /// question : mesure sur la borne le 2026-09-30, 289 processus, 7 ms le parcours, pres de
    /// 320 ms les neuf. Le panneau de classement le payait avant de s'ouvrir (216 a 1 150 ms
    /// d'« conditions »), et la surveillance du panel toutes les 5 s pendant une partie lancee
    /// hors d'ES.
    /// </summary>
    /// <summary>
    /// Le RetroArch du RENDU VIDEO d'un replay (role « station ») : il tourne cache, hors de l'ecran. Ce n'est pas
    /// une partie : les panneaux d'ES ne doivent pas le prendre pour un jeu (vu le 22/10 : plus aucun panneau ne
    /// s'ouvrait pendant les trois minutes d'un rendu).
    /// </summary>
    private static volatile int _processusDuRendu;

    public static void NoterLeRendu(int processus) => _processusDuRendu = processus;

    public static void OublierLeRendu() => _processusDuRendu = 0;

    /// <summary>Un rendu video tourne-t-il ? Son wrapper rend alors aussi un bilan de fin, qui n'est pas celui de la partie.</summary>
    public static bool RenduEnCours => _processusDuRendu != 0;

    /// <summary>Un emulateur tourne-t-il pour une PARTIE (le RetroArch d'un rendu video ne compte pas) ?</summary>
    public static bool EmulateurDeJeuTourne() => EmulateurDeJeuTourne(null);

    /// <summary>
    /// Le meme, pour un emulateur demarre apres <paramref name="demarreApres"/> (heure locale) : celui d'un lancement
    /// precis. Un RetroArch deja ouvert ne passe pas pour le jeu qui demarre (25/10 : un RetroArch de banc a fait effacer
    /// « Les joueurs se synchronisent… » 48 s avant l'arrivee du vrai jeu, et la salle a ete quittee entre-temps).
    /// </summary>
    public static bool EmulateurDeJeuTourne(DateTime? demarreApres)
    {
        var rendu = _processusDuRendu;
        Process[] tous;
        try
        {
            tous = Process.GetProcesses();
        }
        catch (Exception)
        {
            return false;
        }

        var trouve = false;
        foreach (var p in tous)
        {
            try
            {
                if (!trouve && p.Id != rendu)
                {
                    var nom = p.ProcessName;
                    trouve = Emulateurs.Any(emulateur => nom.StartsWith(emulateur, StringComparison.OrdinalIgnoreCase))
                        && (demarreApres is not { } depuis || p.StartTime >= depuis);
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                p.Dispose();
            }
        }

        return trouve;
    }

    /// <summary>
    /// Le RetroArch d'un rendu a pris le premier plan en demarrant (Windows active une fenetre lancee « reduite ») :
    /// ES perdait la main, et la manette avec. On la lui rend, seulement si c'est bien cette fenetre-la qui est
    /// devant : un jeu lance entre-temps garde la sienne.
    /// </summary>
    public static void RendreAEsSiLeRenduEstDevant(Process rendu)
    {
        try
        {
            var fenetre = rendu.MainWindowHandle;
            if (fenetre != IntPtr.Zero && GetForegroundWindow() == fenetre)
            {
                FocusEmulationStation();
            }
        }
        catch (Exception)
        {
            // Un rendu qui s'arrete entre-temps n'a plus de fenetre : rien a rendre.
        }
    }

    /// <summary>
    /// La fenetre de premier plan est-elle celle du RetroArch cache d'un rendu video ? Les panneaux d'ES ne se
    /// ferment pas pour lui : ils rendent le premier plan a ES (le rendu le reprend parfois en cours de route).
    /// </summary>
    public static bool RenduEstDevant()
    {
        var rendu = _processusDuRendu;
        if (rendu == 0)
        {
            return false;
        }

        var devant = GetForegroundWindow();
        if (devant == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(devant, out var pid);
        return pid == (uint) rendu;
    }

    public static bool EmulateurTourne()
    {
        Process[] tous;
        try
        {
            tous = Process.GetProcesses();
        }
        catch (Exception)
        {
            return false;
        }

        var trouve = false;
        foreach (var p in tous)
        {
            try
            {
                if (!trouve)
                {
                    var nom = p.ProcessName;
                    foreach (var emulateur in Emulateurs)
                    {
                        if (nom.StartsWith(emulateur, StringComparison.OrdinalIgnoreCase))
                        {
                            trouve = true;
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Un processus termine entre-temps n'a plus de nom : on passe.
            }
            finally
            {
                p.Dispose();
            }
        }

        return trouve;
    }

    /// <summary>
    /// La fenetre de premier plan appartient-elle a ce processus ? Sans rien enumerer : on part de
    /// la fenetre devant et on lit le nom de SON processus.
    /// </summary>
    public static bool DevantEst(string prefixe)
    {
        try
        {
            var devant = GetForegroundWindow();
            if (devant == IntPtr.Zero) return false;
            GetWindowThreadProcessId(devant, out var pid);
            if (pid == 0) return false;
            using var p = Process.GetProcessById((int) pid);
            return p.ProcessName.StartsWith(prefixe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// La fenetre d'EmulationStation, par son titre ; a defaut, par son processus. Sert a trouver
    /// l'ecran qui la porte sans parcourir tous les processus a chaque ouverture du panneau.
    /// </summary>
    public static IntPtr FenetreDEmulationStation()
    {
        var h = FindWindowW(null, "EmulationStation");
        if (h != IntPtr.Zero) return h;
        foreach (var p in Processus("emulationstation"))
        {
            using (p)
            {
                if (h == IntPtr.Zero && p.MainWindowHandle != IntPtr.Zero) h = p.MainWindowHandle;
            }
        }

        return h;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

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

    /// <summary>
    /// GARDE UNE FENETRE HORS DE TOUS LES ECRANS, sans la reduire (CDC video V2, §4.2). Le rendu video d'un
    /// replay filme l'image affichee par OpenGL : une fenetre REDUITE n'a plus d'image (film noir, vecu le
    /// 14 octobre), alors qu'une fenetre hors ecran garde la sienne. RetroArch demarre reduit, a sa taille ;
    /// on le restaure ici sans l'activer, loin a gauche et en haut, et on lui retire son bouton de barre des
    /// taches. Rejoue a chaque appel : une fenetre qui revient a l'ecran repart. Rend vrai si elle existe.
    /// </summary>
    public static bool GarderHorsEcran(Process process)
    {
        try
        {
            process.Refresh();
            var fenetre = process.MainWindowHandle;
            if (fenetre == IntPtr.Zero) return false;

            var style = GetWindowLongPtr(fenetre, GwlExStyle).ToInt64();
            var voulu = (style | WsExToolWindow) & ~WsExAppWindow;
            if (voulu != style) SetWindowLongPtr(fenetre, GwlExStyle, new IntPtr(voulu));

            var placement = new Placement { Taille = Marshal.SizeOf<Placement>() };
            if (!GetWindowPlacement(fenetre, ref placement)) return true;
            var largeur = placement.Normale.Droite - placement.Normale.Gauche;
            var hauteur = placement.Normale.Bas - placement.Normale.Haut;
            if (IsIconic(fenetre) || placement.Normale.Gauche > HorsEcran / 2)
            {
                placement.Normale = new Rectangle
                {
                    Gauche = HorsEcran, Haut = HorsEcran, Droite = HorsEcran + largeur, Bas = HorsEcran + hauteur,
                };
                placement.Affichage = SwShowNoActivate;
                SetWindowPlacement(fenetre, ref placement);
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Loin de tout ecran, a gauche et en haut (assez pour un mur d'ecrans, sans toucher -32000).</summary>
    private const int HorsEcran = -12000;
    private const int SwShowNoActivate = 4;
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private const long WsExAppWindow = 0x40000;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Gauche, Haut, Droite, Bas;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public int Taille;
        public int Drapeaux;
        public int Affichage;
        public int MinX, MinY, MaxX, MaxY;
        public Rectangle Normale;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref Placement placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref Placement placement);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr valeur);

    /// <summary>
    /// Le premier emulateur de la liste qui a une fenetre, devant ou non, en UN parcours des
    /// processus ; null s'il n'y en a pas encore.
    ///
    /// L'attente de la fenetre au lancement en faisait un par nom d'emulateur, soit neuf toutes les
    /// 600 ms pendant tout le chargement du jeu : le plus gros du processeur de l'API a ce moment,
    /// presque tout en temps systeme (mesure sur la borne le 2026-09-30).
    /// </summary>
    private static string? EmulateurAvecFenetre()
    {
        Process[] tous;
        try
        {
            tous = Process.GetProcesses();
        }
        catch (Exception)
        {
            return null;
        }

        var meilleur = -1;
        foreach (var p in tous)
        {
            try
            {
                var nom = p.ProcessName;
                for (var i = 0; i < Emulateurs.Length && (meilleur < 0 || i < meilleur); i++)
                {
                    // La fenetre n'est demandee qu'a un emulateur : c'est elle qui coute.
                    if (nom.StartsWith(Emulateurs[i], StringComparison.OrdinalIgnoreCase) && p.MainWindowHandle != IntPtr.Zero)
                    {
                        meilleur = i;
                        break;
                    }
                }
            }
            catch (Exception)
            {
                // Un processus termine entre-temps n'a plus de nom : on passe.
            }
            finally
            {
                p.Dispose();
            }
        }

        return meilleur < 0 ? null : Emulateurs[meilleur];
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

            vu = EmulateurAvecFenetre();
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
