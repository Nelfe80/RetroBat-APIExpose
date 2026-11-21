using System.Xml.Linq;

namespace RetroBat.Api.Media;

/// <summary>
/// Entrees de gamelist qui ne designent aucun fichier : le pseudo-jeu d'une liste vide d'ES
/// ("&lt;Aucune entree trouvee&gt;", "&lt;No entries found&gt;"...) et tout chemin contenant un caractere
/// interdit dans un chemin Windows. emulatorLauncher parcourt la gamelist du systeme quand on ne lui
/// donne pas -gameinfo et plante sur <c>Path.Combine</c> a la premiere entree de ce genre.
/// Calculs purs, sans entree-sortie.
/// </summary>
public static class GamelistEntreesInvalides
{
    private static readonly char[] Interdits = { '<', '>', '|', '"', '*', '?' };

    /// <summary>Vrai si le chemin (relatif ES ou absolu) peut designer un fichier Windows.</summary>
    public static bool CheminDeJeuValide(string? chemin)
    {
        if (string.IsNullOrWhiteSpace(chemin))
        {
            return false;
        }

        foreach (var c in chemin)
        {
            if (c < ' ' || Array.IndexOf(Interdits, c) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Vrai pour un nom entre chevrons, comme le pseudo-jeu d'une liste vide d'ES.</summary>
    public static bool NomDePseudoJeu(string? nom)
    {
        var n = nom?.Trim();
        return !string.IsNullOrEmpty(n) && n.Length >= 2 && n[0] == '<' && n[^1] == '>';
    }

    public static bool EntreeInvalide(XElement game)
    {
        var chemin = game.Element("path")?.Value;
        return !CheminDeJeuValide(chemin) || NomDePseudoJeu(game.Element("name")?.Value);
    }

    /// <summary>Retire de la racine les entrees invalides ; rend leur nombre.</summary>
    public static int Retirer(XElement? root)
    {
        if (root == null)
        {
            return 0;
        }

        var aRetirer = root.Elements("game").Where(EntreeInvalide).ToList();
        foreach (var game in aRetirer)
        {
            game.Remove();
        }

        return aRetirer.Count;
    }
}
