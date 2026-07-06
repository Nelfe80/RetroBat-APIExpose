using System.Runtime.InteropServices;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Hides our own console window (--hide-console). The ES hook starts the exe
/// directly and lets it hide itself: no PowerShell window tricks, which are
/// flagged by antivirus ClickFix heuristics.
/// </summary>
public static class ConsoleWindowNative
{
    private const int SwHide = 0;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static void Hide()
    {
        var handle = GetConsoleWindow();
        if (handle != IntPtr.Zero)
        {
            ShowWindow(handle, SwHide);
        }
    }
}
