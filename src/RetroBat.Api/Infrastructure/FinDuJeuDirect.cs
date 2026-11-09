using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LA FIN D'UN JEU LANCE PAR L'API, SANS ATTENDRE LE LANCEUR (09/11).
///
/// <see cref="LancementDirect"/> annoncait la fin quand emulatorLauncher rendait la main. Le 09/11, apres une erreur de
/// FBNeo, il l'a rendue 48 s apres la fermeture de RetroArch : pendant ce temps, le Deck restait pose sur le menu d'ES et
/// le panneau de classement refusait de s'ouvrir (« un jeu tourne »). La partie se termine donc des que le RetroArch DU
/// LANCEUR disparait, processus ou fenetre ; la sortie du lanceur reste le filet (RetroArch jamais vu, autre emulateur).
///
/// Le RetroArch suivi est un descendant du lanceur : ni le RetroArch cache d'un rendu video, ni un spectateur, ni un banc
/// ouvert a cote ne passent pour le jeu.
/// </summary>
internal sealed class GuetDeLaFinDuJeu
{
    /// <summary>Le pas du relevé. Deux relevés de suite sans processus : la fin tombe entre 0,5 et 1 s apres la sortie.</summary>
    public static readonly TimeSpan Pas = TimeSpan.FromMilliseconds(500);

    /// <summary>Relevés de suite sans processus avant d'annoncer la fin (un relevé manqué ne termine pas une partie).</summary>
    public const int AbsencesDuProcessus = 2;

    /// <summary>
    /// Relevés de suite sans fenetre, le processus encore la, avant d'annoncer la fin (2 s). RetroArch recree sa fenetre au
    /// passage en plein ecran : une absence breve n'est pas une fin. Une fenetre jamais vue ne compte pas (chargement).
    /// </summary>
    public const int AbsencesDeLaFenetre = 4;

    private bool _processusVu;
    private bool _fenetreVue;
    private int _sansProcessus;
    private int _sansFenetre;

    /// <summary>Le RetroArch du lanceur a-t-il deja ete vu ? Sans lui, seule la sortie du lanceur termine la partie.</summary>
    public bool ProcessusVu => _processusVu;

    /// <summary>
    /// Un relevé. Rend la raison de la fin (« processus » ou « fenetre »), ou null tant que le jeu tourne ou n'a pas
    /// encore demarre.
    /// </summary>
    public string? Relever(bool processusLa, bool fenetreLa)
    {
        if (processusLa)
        {
            _processusVu = true;
            _sansProcessus = 0;
            if (fenetreLa)
            {
                _fenetreVue = true;
                _sansFenetre = 0;
                return null;
            }

            if (!_fenetreVue) return null;
            return ++_sansFenetre >= AbsencesDeLaFenetre ? "fenetre" : null;
        }

        if (!_processusVu) return null;
        return ++_sansProcessus >= AbsencesDuProcessus ? "processus" : null;
    }
}

/// <summary>Les processus de la machine, par un seul instantane (Toolhelp) : numero, parent et nom de l'exe.</summary>
[SupportedOSPlatform("windows")]
internal static class ArbreDesProcessus
{
    internal readonly record struct Entree(int Id, int Parent, string Exe);

    /// <summary>Profondeur cherchee sous le lanceur (lanceur, eventuel intermediaire, RetroArch).</summary>
    private const int Profondeur = 4;

    /// <summary>
    /// Les RetroArch (nom d'exe qui COMMENCE par « retroarch » : sur la borne, « retroarch.patched.RETROBAT.exe ») qui
    /// descendent de <paramref name="racine"/>.
    /// </summary>
    internal static List<int> RetroArchSous(IReadOnlyList<Entree> processus, int racine)
    {
        var parents = new Dictionary<int, int>(processus.Count);
        foreach (var p in processus) parents[p.Id] = p.Parent;

        var trouves = new List<int>();
        foreach (var p in processus)
        {
            if (p.Id == racine || !p.Exe.StartsWith("retroarch", StringComparison.OrdinalIgnoreCase)) continue;
            var courant = p.Parent;
            for (var i = 0; i < Profondeur && courant > 0; i++)
            {
                if (courant == racine)
                {
                    trouves.Add(p.Id);
                    break;
                }

                // Un parent mort laisse un numero qui ne mene nulle part ; un numero repris qui boucle s'arrete ici.
                if (!parents.TryGetValue(courant, out var suivant) || suivant == courant) break;
                courant = suivant;
            }
        }

        return trouves;
    }

    /// <summary>L'instantane. Null si Windows le refuse : le relevé est alors saute, il ne compte jamais pour une fin.</summary>
    internal static List<Entree>? Instantane()
    {
        var cliche = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (cliche == IntPtr.Zero || cliche == new IntPtr(-1)) return null;
        try
        {
            var liste = new List<Entree>(320);
            var e = new ProcessEntry32 { dwSize = (uint) Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(cliche, ref e)) return null;
            do
            {
                liste.Add(new Entree((int) e.th32ProcessID, (int) e.th32ParentProcessID, e.szExeFile ?? ""));
            }
            while (Process32NextW(cliche, ref e));
            return liste;
        }
        finally
        {
            CloseHandle(cliche);
        }
    }

    /// <summary>Une fenetre visible de premier niveau appartient-elle a l'un de ces processus ?</summary>
    internal static bool FenetreVisible(IReadOnlyList<int> processus)
    {
        if (processus.Count == 0) return false;
        var vue = false;
        EnumWindows((fenetre, _) =>
        {
            if (!IsWindowVisible(fenetre)) return true;
            GetWindowThreadProcessId(fenetre, out var pid);
            foreach (var p in processus)
            {
                if (p == (int) pid)
                {
                    vue = true;
                    return false;
                }
            }

            return true;
        }, IntPtr.Zero);
        return vue;
    }

    private const uint Th32csSnapProcess = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private delegate bool EnumWindowsProc(IntPtr fenetre, IntPtr parametre);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processus);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr cliche, ref ProcessEntry32 entree);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr cliche, ref ProcessEntry32 entree);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc rappel, IntPtr parametre);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr fenetre);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr fenetre, out uint processus);
}
