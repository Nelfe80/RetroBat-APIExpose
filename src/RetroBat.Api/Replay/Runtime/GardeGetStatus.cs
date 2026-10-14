using System.Diagnostics;
using System.Text.RegularExpressions;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Runtime;

/// <summary>
/// GET_STATUS FAIT PLANTER RETROARCH 1.22.2 AVEC UN COEUR SANS « systemid » (vu le 14 octobre 2026, chez Another Kif
/// puis reproduit sur la borne de dev). Pour repondre, RetroArch recopie core_info->system_id, l'identifiant de
/// systeme du fichier .info du coeur, sans verifier qu'il existe. Un coeur ajoute sans son .info (amiberry, telecharge
/// a part) n'en a pas : RetroArch plante au premier GET_STATUS, toujours au meme endroit (retroarch.exe +1af761).
/// RetroArch l'a corrige depuis (« if (core_info &amp;&amp; core_info->system_id) »), mais pas dans la 1.22.2. Or
/// APIExpose l'interroge par GET_STATUS a chaque partie, des le lancement : le jeu plantait aussitot, et se lancait
/// sans le plugin.
/// <para>On n'envoie donc pas GET_STATUS quand le coeur charge, lu dans le journal de lancement d'ES, n'a pas
/// d'identifiant de systeme dans son .info. Sans journal de ce RetroArch (lancement hors ES), rien ne change.</para>
/// </summary>
public static class GardeGetStatus
{
    private static readonly Regex LigneDuCoeur = new(
        "Loading dynamic libretro core from:\\s*\"(?<chemin>[^\"]+)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LigneDuSysteme = new(
        "^\\s*systemid\\s*=\\s*\"(?<id>[^\"]*)\"", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly object Verrou = new();
    private static int _processus;
    private static bool _sansDanger = true;
    private static string? _coeur;

    /// <summary>Le coeur dont le .info n'a pas d'identifiant de systeme, si GET_STATUS est evite ; sinon null.</summary>
    public static string? CoeurEvite
    {
        get { lock (Verrou) return _sansDanger ? null : _coeur; }
    }

    /// <summary>Vrai si GET_STATUS ne risque pas de faire planter le RetroArch en cours.</summary>
    public static bool SansDanger()
    {
        try
        {
            var (pid, debut) = RetroArchEnCours();
            if (pid == 0) return true;
            lock (Verrou)
            {
                if (pid == _processus) return _sansDanger;
            }

            var journal = new FileInfo(RetroBatPaths.EsLaunchStdoutLogPath);
            // Le journal doit etre celui de CE RetroArch : recrit a son lancement. Sinon, on ne sait rien.
            if (!journal.Exists || (debut is { } d && journal.LastWriteTime < d.AddSeconds(-5)))
                return Retenir(pid, true, null);

            List<string> lignes;
            using (var flux = new FileStream(journal.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var lecteur = new StreamReader(flux))
            {
                lignes = new List<string>();
                string? ligne;
                while ((ligne = lecteur.ReadLine()) is not null) lignes.Add(ligne);
            }
            var coeur = CoeurDuJournal(lignes);
            // Le coeur n'est pas encore ecrit : prudence jusqu'a ce qu'il le soit (quelques instants).
            if (coeur is null) return false;

            var info = Path.Combine(Path.GetDirectoryName(RetroBatPaths.RetroArchConfigPath)!, "info",
                Path.GetFileNameWithoutExtension(coeur) + ".info");
            return Retenir(pid, File.Exists(info) && DeclareUnSysteme(File.ReadAllText(info)), Path.GetFileName(coeur));
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool Retenir(int pid, bool sansDanger, string? coeur)
    {
        lock (Verrou)
        {
            _processus = pid;
            _sansDanger = sansDanger;
            _coeur = coeur;
        }
        return sansDanger;
    }

    private static (int Pid, DateTime? Debut) RetroArchEnCours()
    {
        var processus = Process.GetProcessesByName("retroarch");
        try
        {
            if (processus.Length == 0) return (0, null);
            var p = processus[0];
            DateTime? debut = null;
            try { debut = p.StartTime; } catch (Exception) { /* processus qui se ferme : debut inconnu */ }
            return (p.Id, debut);
        }
        finally
        {
            foreach (var p in processus) p.Dispose();
        }
    }

    /// <summary>Le dernier coeur charge selon le journal de RetroArch. Pur, pour etre teste.</summary>
    internal static string? CoeurDuJournal(IEnumerable<string> lignes)
    {
        string? coeur = null;
        foreach (var ligne in lignes)
        {
            var m = LigneDuCoeur.Match(ligne);
            if (m.Success) coeur = m.Groups["chemin"].Value;
        }
        return coeur;
    }

    /// <summary>Le .info declare-t-il un identifiant de systeme non vide ? Pur, pour etre teste.</summary>
    internal static bool DeclareUnSysteme(string? info)
    {
        if (string.IsNullOrEmpty(info)) return false;
        var m = LigneDuSysteme.Match(info);
        return m.Success && m.Groups["id"].Value.Trim().Length > 0;
    }
}
