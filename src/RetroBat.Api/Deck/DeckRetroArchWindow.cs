using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RetroBat.Api.Deck;

/// <summary>
/// La zone cliente de la fenetre de RetroArch, en pixels PHYSIQUES de l'ecran (bureau etendu : coordonnees negatives
/// possibles). L'API n'est pas declaree sensible a la DPI pour tout le processus ; la mesure se fait donc dans un
/// contexte « par moniteur V2 » pose sur le fil le temps des appels, puis rendu : sans lui, Windows rendrait des
/// coordonnees virtualisees sur un ecran a 125 %. Lecture seule : rien n'est deplace ni redimensionne.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DeckRetroArchWindow
{
    /// <summary>La zone cliente et l'identifiant du processus, ou null si aucune fenetre de RetroArch n'est visible.</summary>
    public static (DeckRect Client, int ProcessId)? Mesurer()
    {
        Process[] processus;
        try
        {
            processus = Process.GetProcessesByName("retroarch");
        }
        catch
        {
            return null;
        }

        try
        {
            foreach (var p in processus)
            {
                IntPtr fenetre;
                try
                {
                    fenetre = p.MainWindowHandle;
                }
                catch
                {
                    continue;
                }

                if (fenetre == IntPtr.Zero || !IsWindowVisible(fenetre) || IsIconic(fenetre)) continue;
                if (Zone(fenetre) is { } zone && !zone.Vide) return (zone, p.Id);
            }
        }
        finally
        {
            foreach (var p in processus) p.Dispose();
        }

        return null;
    }

    private static DeckRect? Zone(IntPtr fenetre)
    {
        var avant = IntPtr.Zero;
        try
        {
            try { avant = SetThreadDpiAwarenessContext(ParMoniteurV2); } catch (EntryPointNotFoundException) { /* Windows 8 et avant */ }
            if (!GetClientRect(fenetre, out var r)) return null;
            var origine = new Point { X = 0, Y = 0 };
            if (!ClientToScreen(fenetre, ref origine)) return null;
            return new DeckRect(origine.X, origine.Y, r.Right - r.Left, r.Bottom - r.Top);
        }
        finally
        {
            if (avant != IntPtr.Zero)
            {
                try { SetThreadDpiAwarenessContext(avant); } catch (EntryPointNotFoundException) { }
            }
        }
    }

    private static readonly IntPtr ParMoniteurV2 = new(-4);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref Point point);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
