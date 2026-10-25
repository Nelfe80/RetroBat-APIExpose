using System.Text.RegularExpressions;

namespace RetroBat.Api.Replay.Video;

/// <summary>
/// LES FILTRES D'IMAGE DU COEUR (decision du user du 25 octobre 2026, films de gogerad aplatis). Un filtre NTSC (Blargg)
/// se fait DANS le coeur : Genesis Plus GX sort alors 640 x 224 au lieu de 320 x 224, avec les franges d'une tele
/// composite, et RetroArch filme cette image. Elle ne se diffuse pas bien : pas de direct ni de film de la partie, le
/// joueur en est prevenu au lancement, et la video du record se fait depuis son replay, rendu sans ses reglages.
///
/// Une option de filtre porte le nom du coeur (genesis_plus_gx_blargg_ntsc_filter, nestopia_blargg_ntsc_filter,
/// snes9x_blargg, fceumm_ntsc_filter, mesen_ntsc_filter...) : seules comptent celles du coeur charge, une autre console
/// peut garder son filtre.
/// </summary>
internal static class FiltreDImage
{
    private static readonly Regex Ligne = new(@"^\s*(?<cle>[A-Za-z0-9_\-]+)\s*=\s*""(?<valeur>[^""]*)""", RegexOptions.CultureInvariant);

    /// <summary>Une option de filtre d'image : NTSC ou Blargg dans son nom. Pur.</summary>
    internal static bool EstUnFiltre(string cle)
        => cle.Contains("blargg", StringComparison.OrdinalIgnoreCase)
           || (cle.Contains("ntsc", StringComparison.OrdinalIgnoreCase) && cle.Contains("filter", StringComparison.OrdinalIgnoreCase));

    /// <summary>Une valeur qui coupe le filtre. Pur.</summary>
    internal static bool EstCoupe(string valeur)
        => valeur.Trim().ToLowerInvariant() is "" or "disabled" or "off" or "false" or "none" or "0" or "no";

    /// <summary>
    /// L'option de filtre active du coeur <paramref name="coeur"/> (son fichier sans « _libretro.dll »), « cle = valeur »,
    /// ou null. Ses variantes (genesis_plus_gx_wide) partagent les options du coeur d'origine. Pur, pour etre teste.
    /// </summary>
    internal static string? Actif(string? coeur, IEnumerable<string> lignesDesOptions)
    {
        if (string.IsNullOrWhiteSpace(coeur)) return null;
        var prefixes = Prefixes(coeur.Trim().ToLowerInvariant());
        foreach (var ligne in lignesDesOptions)
        {
            var m = Ligne.Match(ligne);
            if (!m.Success) continue;
            var cle = m.Groups["cle"].Value;
            var valeur = m.Groups["valeur"].Value;
            if (EstUnFiltre(cle) && !EstCoupe(valeur) && prefixes.Any(p => cle.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                return cle + " = " + valeur;
        }
        return null;
    }

    /// <summary>
    /// Le nom du coeur, et celui de son coeur d'origine pour une variante (genesis_plus_gx_wide : genesis_plus_gx), suivis
    /// d'un « _ ». Jamais plus court : « genesis_ » serait celui de plusieurs coeurs.
    /// </summary>
    private static List<string> Prefixes(string coeur)
    {
        var prefixes = new List<string> { coeur + "_" };
        var coupe = coeur.LastIndexOf('_');
        if (coupe >= 5) prefixes.Add(coeur[..coupe] + "_");
        return prefixes;
    }

    /// <summary>
    /// Les options des coeurs, chaque filtre d'image coupe (le rendu d'un replay se fait « sans contrainte », decision du
    /// 25 octobre 2026) ; le reste tel quel, le replay en depend. Pur.
    /// </summary>
    internal static IEnumerable<string> SansFiltres(IEnumerable<string> lignes)
    {
        foreach (var ligne in lignes)
        {
            var m = Ligne.Match(ligne);
            yield return m.Success && EstUnFiltre(m.Groups["cle"].Value) && !EstCoupe(m.Groups["valeur"].Value)
                ? m.Groups["cle"].Value + " = \"disabled\""
                : ligne;
        }
    }

    /// <summary>
    /// Le fichier des options des coeurs que RetroArch lit : <c>core_options_path</c> de retroarch.cfg s'il est donne, sinon
    /// retroarch-core-options.cfg a cote de retroarch.cfg.
    /// </summary>
    internal static string FichierDesOptions(string retroarchCfg, string? coreOptionsPath)
        => !string.IsNullOrWhiteSpace(coreOptionsPath) && Path.IsPathRooted(coreOptionsPath)
            ? coreOptionsPath
            : Path.Combine(Path.GetDirectoryName(retroarchCfg) ?? ".", "retroarch-core-options.cfg");
}
