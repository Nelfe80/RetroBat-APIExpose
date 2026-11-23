using System.Text.Json;
using RetroBat.Api.Scoring;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Les calculs PURS de la capture de record par classement (decision du user du 23/11 : chaque classement a sa
/// capture, 1CC comme 1LC et modes speciaux). Separes de <see cref="ScoreShotService"/> pour se tester sans reseau.
/// </summary>
internal static class ScoreShotRegles
{
    /// <summary>Au plus trois photos par partie : un jeu a modes peut avoir plusieurs regles solo, et la regle jouee ne
    /// se connait qu'a la fin ; sans plafond, un joueur qui franchit les records de plusieurs modes verrait RetroArch
    /// annoncer « capture » a chaque fois (retour de la flotte du 21/09).</summary>
    public const int PhotosMaxParPartie = 3;

    /// <summary>
    /// Les regles d'un jeu qui se photographient : celles d'une partie SEULE, au score. Ni le speedrun (se classe au
    /// temps, deja ecarte plus haut en epreuve), ni le MULTI (1cc-multi, 1lc-multi : une partie a plusieurs). Le 1CC, le
    /// 1LC et leurs modes (1cc-extended, 1cc-special, 1lc-power-up...) y sont.
    /// </summary>
    public static string[] ReglesPhotographiables(string corpsDuProfil)
    {
        var profils = NelfePlayScoringReporter.ProfilsDuCorps(corpsDuProfil);
        if (profils is null) return Array.Empty<string>();
        var vues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sortie = new List<string>();
        foreach (var p in profils)
        {
            if (!p.TryGetProperty("ruleset", out var r) || r.ValueKind != JsonValueKind.String) continue;
            var regle = r.GetString();
            if (string.IsNullOrWhiteSpace(regle)) continue;
            if (ModesDeJeu.EstSpeedrun(regle) || ModesDeJeu.EstMulti(regle) || ModesDeJeu.Est1LCMulti(regle)) continue;
            if (vues.Add(regle)) sortie.Add(regle);
        }
        return sortie.ToArray();
    }

    /// <summary>Le score franchit-il le seuil ? Seuil nul : le tableau n'est pas plein, tout score positif entre.</summary>
    public static bool Franchit(long? seuil, bool plusPetitEstMeilleur, long total)
        => seuil is null
            ? total > 0
            : (plusPetitEstMeilleur ? total < seuil.Value : total > seuil.Value);

    /// <summary>Un nom de fichier sur pour une regle (jamais un chemin).</summary>
    public static string NomDeFichier(string regle)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in regle.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-');
        return sb.Length == 0 ? "regle" : sb.ToString();
    }
}
