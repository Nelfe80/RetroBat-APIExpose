using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LE SENS D'UN JEU VERTICAL (decision du user du 09/11). Le referentiel dit si un jeu est vertical
/// (<c>GamelistIdentity.EstVertical</c>) ; ce qui manquait, c'est le sens : un quart de tour dans un sens (ROT90) ou dans
/// l'autre (ROT270). Le coeur l'annonce d'ordinaire (<c>SET_ROTATION</c> dans le journal de lancement) ; quand il se tait
/// (coeur MAME de RetroArch : Contra de Getupor, 06/11, « rotation du coeur : inconnue »), la capture s'arretait.
/// Les sources, dans l'ordre :
/// <list type="number">
/// <item>l'image du film : deja en portrait, le coeur l'a redressee lui-meme, aucun quart de tour (0) ;</item>
/// <item>la base de MAME : <c>mame.exe -listxml &lt;set&gt;</c> donne <c>display rotate</c> (90, 180, 270), mis en cache sur la
/// borne. Releve du 09/11 : bgaregga 270, contra 90.</item>
/// </list>
/// Les quarts de tour sont ceux de libretro (antihoraires) : MAME ROT90 (horaire) = 3, ROT270 = 1, ROT180 = 2 ; Ms. Pac-Man,
/// ROT90 chez MAME, s'annonce bien 3 sous FBNeo.
/// </summary>
public static class SensDesJeuxVerticaux
{
    private static readonly object Verrou = new();
    private static Dictionary<string, int?>? _cache;

    /// <summary>Le fichier du cache des sens lus dans MAME.</summary>
    internal static string CheminDuCache => Path.Combine(RetroBatPaths.PluginRoot, "state", "nelfenet", "replay", "sens-des-jeux.json");

    /// <summary>L'executable de MAME de RetroBat.</summary>
    internal static string ExeDeMame => Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "mame", "mame.exe");

    /// <summary>
    /// Les quarts de tour (libretro) qui redressent l'image du film d'un jeu vertical, ou null s'ils restent inconnus.
    /// <paramref name="largeur"/> et <paramref name="hauteur"/> : l'image du film (en-tete) ; <paramref name="sensDeMame"/> :
    /// la rotation de la base de MAME en quarts de tour libretro. Pur.
    /// </summary>
    public static int? QuartsDeTour(long largeur, long hauteur, int? sensDeMame)
    {
        if (largeur <= 0 || hauteur <= 0) return null;
        if (hauteur > largeur) return 0;
        return sensDeMame is 1 or 3 ? sensDeMame : null;
    }

    /// <summary>La rotation de MAME (degres horaires) en quarts de tour libretro (antihoraires). Pur.</summary>
    public static int? DepuisMame(int degres) => degres switch
    {
        0 => 0,
        90 => 3,
        180 => 2,
        270 => 1,
        _ => null,
    };

    /// <summary>La rotation (degres) du premier ecran de <paramref name="set"/> dans une sortie de -listxml, ou null. Pur.</summary>
    public static int? RotationDuListXml(string xml, string set)
    {
        var debut = xml.IndexOf("<machine name=\"" + set + "\"", StringComparison.OrdinalIgnoreCase);
        if (debut < 0) return null;
        var fin = xml.IndexOf("</machine>", debut, StringComparison.OrdinalIgnoreCase);
        var machine = fin > debut ? xml[debut..fin] : xml[debut..];
        var ecran = Regex.Match(machine, @"<display\b[^>]*\brotate=""(\d+)""");
        return ecran.Success && int.TryParse(ecran.Groups[1].Value, out var r) ? r : null;
    }

    /// <summary>
    /// Le sens de <paramref name="set"/> selon la base de MAME (quarts de tour libretro), lu une fois puis gardé ; null sans
    /// MAME sur la borne ou pour un set qu'il ne connait pas.
    /// </summary>
    public static int? SensDeMame(string? set)
    {
        if (string.IsNullOrWhiteSpace(set) || set.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || set.Contains(' ')) return null;
        lock (Verrou)
        {
            _cache ??= LireLeCache();
            if (_cache.TryGetValue(set, out var connu)) return connu;
        }
        var lu = LireDansMame(set);
        lock (Verrou)
        {
            _cache![set] = lu;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CheminDuCache)!);
                File.WriteAllText(CheminDuCache, JsonSerializer.Serialize(_cache));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* le cache n'est qu'un raccourci */ }
        }
        return lu;
    }

    private static Dictionary<string, int?> LireLeCache()
    {
        try
        {
            if (File.Exists(CheminDuCache))
                return JsonSerializer.Deserialize<Dictionary<string, int?>>(File.ReadAllText(CheminDuCache))
                    is { } d ? new Dictionary<string, int?>(d, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* refait */ }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private static int? LireDansMame(string set)
    {
        var exe = ExeDeMame;
        if (!File.Exists(exe)) return null;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-listxml");
            psi.ArgumentList.Add(set);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var sortie = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15_000))
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception) { /* deja parti */ }
                return null;
            }
            return RotationDuListXml(sortie.GetAwaiter().GetResult(), set) is { } degres ? DepuisMame(degres) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
