using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// OU RETROARCH ECRIT SES CAPTURES, ET COMMENT Y RETROUVER LA SIENNE (2026-10-10). La photo d'un
/// record et sa regeneration demandent une capture a RetroArch, puis adoptent le fichier qui vient
/// d'apparaitre. Chez un joueur, deux records de suite n'ont donne aucune photo, sans une ligne pour
/// le dire : la recherche echouait en silence.
///
/// On lit la configuration de RetroArch plutot que de supposer, et on couvre ce qu'elle permet :
/// - un chemin absolu, ce que pose RetroBat ;
/// - un chemin qui commence par « : », le dossier de RetroArch lui-meme ;
/// - le rangement par contenu (sort_screenshots_by_content_enable) : un sous-dossier par jeu, d'ou
///   la recherche a un niveau de profondeur ;
/// - un dossier qui n'existe pas encore : RetroArch peut le creer en ecrivant, on le guette.
/// </summary>
internal static class CapturesRetroArch
{
    private static string DossierDeRetroArch => Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch");

    /// <summary>Les dossiers ou chercher, existants ou non : le dossier configure d'abord, puis l'habituel.</summary>
    public static IReadOnlyList<string> Dossiers()
    {
        var dossiers = new List<string>();
        try
        {
            var cfg = Path.Combine(DossierDeRetroArch, "retroarch.cfg");
            if (File.Exists(cfg) && LireDossier(File.ReadLines(cfg), DossierDeRetroArch) is { } lu) dossiers.Add(lu);
        }
        catch
        {
            // Configuration illisible : le dossier habituel reste.
        }

        var habituel = Path.Combine(RetroBatPaths.RetroBatRoot, "screenshots");
        if (!dossiers.Any(d => MemeDossier(d, habituel))) dossiers.Add(habituel);
        return dossiers;
    }

    /// <summary>
    /// La valeur de screenshot_directory, le « : » de RetroArch resolu. Null si la cle manque, est vide
    /// ou vaut « default ».
    /// </summary>
    internal static string? LireDossier(IEnumerable<string> lignes, string dossierDeRetroArch)
    {
        foreach (var ligne in lignes)
        {
            var eq = ligne.IndexOf('=');
            if (eq < 0 || !ligne[..eq].Trim().Equals("screenshot_directory", StringComparison.Ordinal)) continue;
            var valeur = ligne[(eq + 1)..].Trim().Trim('"');
            if (valeur.Length == 0 || valeur.Equals("default", StringComparison.OrdinalIgnoreCase)) return null;
            if (valeur[0] != ':') return valeur;
            var reste = valeur[1..].TrimStart('\\', '/');
            return reste.Length == 0 ? dossierDeRetroArch : Path.Combine(dossierDeRetroArch, reste);
        }
        return null;
    }

    /// <summary>
    /// La capture la plus recente ecrite depuis <paramref name="apres"/>, dans le dossier ou l'un de ses
    /// sous-dossiers directs. Un fichier encore vide (en cours d'ecriture) ne compte pas.
    /// </summary>
    public static FileInfo? PlusRecente(string dossier, DateTime apres)
    {
        var racine = new DirectoryInfo(dossier);
        if (!racine.Exists) return null;
        IEnumerable<DirectoryInfo> sousDossiers;
        try { sousDossiers = racine.EnumerateDirectories().ToList(); }
        catch { sousDossiers = Array.Empty<DirectoryInfo>(); }
        return new[] { racine }.Concat(sousDossiers)
            .SelectMany(Captures)
            .Where(f => f.LastWriteTimeUtc >= apres && f.Length > 0)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static IEnumerable<FileInfo> Captures(DirectoryInfo dossier)
    {
        try { return dossier.EnumerateFiles("*.png").ToList(); }
        catch { return Array.Empty<FileInfo>(); }
    }

    private static bool MemeDossier(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
