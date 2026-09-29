using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// UNE FENETRE NE DOIT JAMAIS FAIRE TOMBER L'API (2026-09-29).
///
/// Un joueur a perdu l'API six fois en une heure, toujours de la meme facon : Windows refusait de
/// creer une fenetre (Win32Exception 1158, le quota de 10 000 objets fenetre du processus etait
/// plein), l'exception partait dans la boucle de messages d'une surimpression, et WinForms voulait
/// alors afficher sa boite « exception non geree »... qu'il ne pouvait pas creer non plus. C'est
/// cette seconde erreur qui tuait le processus : plus de bandeau « partie certifiable », plus de
/// mesure, jusqu'au redemarrage de RetroBat.
///
/// Deux filets, pour toutes les surimpressions :
///   - un gestionnaire GLOBAL des exceptions de fenetre, qui journalise au lieu d'afficher ;
///   - <see cref="Run"/> autour du corps de chaque fil de fenetre, pour ce qui echoue avant la
///     boucle de messages (la creation meme de la fenetre).
/// Une surimpression qui echoue se tait ; le scoring, lui, n'a besoin d'aucune fenetre.
///
/// Le quota plein n'est que le symptome d'une fuite ailleurs : le journal dit desormais combien
/// d'objets le processus tient (voir <see cref="GuiResourceMonitorService"/>).
/// </summary>
public static class OverlayUiGuard
{
    private static ILogger? _logger;
    private static readonly object Gate = new();
    private static DateTime _derniereAlerte = DateTime.MinValue;
    private static int _taisees;

    /// <summary>A appeler AVANT toute fenetre : WinForms refuse de changer de mode ensuite.</summary>
    public static void Installer()
    {
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        }
        catch (InvalidOperationException)
        {
            // Une fenetre existe deja : le mode par defaut est deja CatchException.
        }

        Application.ThreadException += (_, e) => Signaler("boucle de fenetre", e.Exception);
    }

    public static void Journal(ILogger logger) => _logger = logger;

    /// <summary>Le corps d'un fil de fenetre, sans qu'une exception puisse en sortir.</summary>
    public static void Run(string nom, Action corps) => Run(nom, null, corps);

    /// <summary>
    /// Idem, et le signal « fenetre prete » part aussi en cas d'echec : l'appelant l'attend souvent
    /// sans delai (StartupOverlayService dans le demarrage meme de l'API), et il trouve ensuite une
    /// surimpression absente, ce qu'il sait deja traiter.
    /// </summary>
    public static void Run(string nom, ManualResetEventSlim? pret, Action corps)
    {
        try
        {
            corps();
        }
        catch (Exception ex)
        {
            Signaler(nom, ex);
            try
            {
                pret?.Set();
            }
            catch (ObjectDisposedException)
            {
                // L'appelant a deja cesse d'attendre.
            }
        }
    }

    /// <summary>
    /// Une alerte toutes les 30 s au plus : un minuteur qui echoue a chaque tic ne doit pas noyer
    /// le journal. On dit combien d'alertes ont ete tues entre deux.
    /// </summary>
    internal static void Signaler(string ou, Exception ex)
    {
        int taisees;
        lock (Gate)
        {
            var maintenant = DateTime.UtcNow;
            if (maintenant - _derniereAlerte < TimeSpan.FromSeconds(30))
            {
                _taisees++;
                return;
            }

            _derniereAlerte = maintenant;
            taisees = _taisees;
            _taisees = 0;
        }

        var (user, gdi) = GuiResources.Lire();
        var message = "Surimpression ({Ou}) en echec, l'API continue : {Message} (objets fenetre {User}, graphiques {Gdi}, {Taisees} alerte(s) tue(s) depuis la precedente)";
        if (_logger is { } journal)
        {
            journal.LogWarning(ex, message, ou, ex.Message, user, gdi, taisees);
        }
        else
        {
            System.Diagnostics.Trace.TraceWarning($"Surimpression ({ou}) en echec : {ex}");
        }
    }
}

/// <summary>Ce que le processus tient d'objets fenetre (USER) et graphiques (GDI).</summary>
public static class GuiResources
{
    private const uint GrGdiObjects = 0;
    private const uint GrUserObjects = 1;

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public static (int User, int Gdi) Lire()
    {
        try
        {
            var p = GetCurrentProcess();
            return ((int)GetGuiResources(p, GrUserObjects), (int)GetGuiResources(p, GrGdiObjects));
        }
        catch
        {
            return (-1, -1);
        }
    }
}
