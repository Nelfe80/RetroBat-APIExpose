using System.Diagnostics;
using System.Text.RegularExpressions;

namespace RetroBat.Api.Replay.Recording;

/// <summary>
/// LE JOURNAL WINDOWS CONFIRME LE PLANTAGE (17/11, rapports d'Old AF Gamer et de NeoGeoff). La garde des replays prenait
/// toute fin de jeu sans fin de partie pour un plantage de RetroArch. Or la fin de partie ne vient que d'un wrapper qui a
/// charge un .MEM : un dump absent de l'index d'alias (SMB3 GoodNES sous un nom TOSEC), un jeu pas encore defini
/// (DaiOuJou Black Label le 14/11) ou une sortie du joueur avant que l'ecoute ne parte ne l'envoient jamais. Chez Old AF
/// Gamer, SMB3 est entre trois fois sans un seul plantage dans le journal ; chez NeoGeoff, Giga Wing et DaiOuJou aussi.
/// Seul Rayforce (11/11) avait vraiment plante.
///
/// Windows note chaque plantage (« Application Error », evenement 1000) avec l'application fautive. On n'y compte que
/// RetroArch, et pas le plantage connu a la fermeture du pilote de manettes SDL2 (ntdll, apres la fin de partie : sans
/// effet), comme l'outil de diagnostic. Null quand le journal ne se lit pas : la garde garde alors son ancien jugement.
/// </summary>
public static class PlantagesWindows
{
    private static readonly Regex Application = new(@"(?:Faulting application name|Nom de l.application d.faillante)\s*:\s*([^,\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Module = new(@"(?:Faulting module name|Nom du module d.faillant)\s*:\s*([^,\r\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Decalage = new(@"(?:Fault offset|D.calage de l.erreur)\s*:\s*0x0*([0-9a-fA-F]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Le message d'un evenement « Application Error » dit-il un plantage de RetroArch en jeu ? Faux pour une autre
    /// application et pour le plantage sans effet a la sortie (ntdll +...fa7d / +...2f6a3). Pur.
    /// </summary>
    public static bool EstUnPlantageEnJeu(string? message)
    {
        if (string.IsNullOrEmpty(message)) return false;
        var application = Application.Match(message);
        if (!application.Success || !application.Groups[1].Value.Trim().StartsWith("retroarch", StringComparison.OrdinalIgnoreCase)) return false;
        var module = Module.Match(message) is { Success: true } m ? m.Groups[1].Value.Trim() : "";
        var decalage = Decalage.Match(message) is { Success: true } d ? d.Groups[1].Value.Trim() : "";
        var aLaSortie = module.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase)
                        && (decalage.EndsWith("fa7d", StringComparison.OrdinalIgnoreCase) || decalage.EndsWith("2f6a3", StringComparison.OrdinalIgnoreCase));
        return !aLaSortie;
    }

    /// <summary>
    /// Windows a-t-il vu RetroArch planter en jeu entre ces deux instants (UTC) ? Null si le journal ne se lit pas, ou
    /// s'il ne remonte pas jusqu'a <paramref name="debutUtc"/> (rien ne prouve alors l'absence de plantage).
    /// </summary>
    public static bool? EnJeuEntre(DateTime debutUtc, DateTime finUtc)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var journal = new EventLog("Application");
            var entrees = journal.Entries;
            if (entrees.Count == 0) return null;
            for (var i = entrees.Count - 1; i >= 0; i--)
            {
                EventLogEntry e;
                try { e = entrees[i]; } catch { continue; }
                var le = e.TimeGenerated.ToUniversalTime();
                if (le < debutUtc) return false;
                if (le > finUtc) continue;
                if (!string.Equals(e.Source, "Application Error", StringComparison.OrdinalIgnoreCase)) continue;
                if (EstUnPlantageEnJeu(e.Message)) return true;
            }
            // Le journal commence apres le debut de la fenetre : on ne sait pas.
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
