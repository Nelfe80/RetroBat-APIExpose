using System.Diagnostics;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// LES PROCESSUS DE RETROARCH, PAR PREFIXE DE NOM (1.9.216). Un hote netplay lance emulatorLauncher sur
/// <c>retroarch.patched.RETROBAT.exe</c> (copie de retroarch.exe) et le spectateur MULTIPLAYER sur une copie sous un autre nom :
/// <c>GetProcessesByName("retroarch")</c> compare le nom ENTIER et ne les voit pas. Sous RetroArch NelfePlay, ce piege decide
/// aussi de la pose de l'exe (jamais pendant une partie). Les processus rendus sont a liberer par l'appelant.
/// </summary>
public static class RetroArchProcessus
{
    public const string Prefixe = "retroarch";

    /// <summary>Ce nom de processus est-il un RetroArch (retroarch, retroarch.patched.RETROBAT, copie du spectateur) ? Pur.</summary>
    public static bool EstUnRetroArch(string? nom) => !string.IsNullOrEmpty(nom) && nom.StartsWith(Prefixe, StringComparison.OrdinalIgnoreCase);

    /// <summary>Tous les RetroArch qui tournent, patched et copies comprises.</summary>
    public static Process[] Tous()
    {
        Process[] processus;
        try { processus = Process.GetProcesses(); }
        catch (Exception) { return []; }
        var gardes = new List<Process>();
        foreach (var p in processus)
        {
            var garder = false;
            try { garder = EstUnRetroArch(p.ProcessName); }
            catch (Exception) { }
            if (garder) gardes.Add(p); else p.Dispose();
        }
        return [.. gardes];
    }
}
