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

    /// <summary>
    /// Vrai si le chemin (relatif ES ou absolu) peut designer un fichier Windows. Les blancs autour du chemin
    /// (gamelist indentee a la main) sont toleres : seul le chemin lui-meme est juge.
    /// </summary>
    public static bool CheminDeJeuValide(string? chemin)
        => !string.IsNullOrWhiteSpace(chemin) && !CheminInterdit(chemin);

    /// <summary>
    /// Vrai si le chemin, blancs autour retires, porte un caractere qu'aucun fichier Windows ne peut avoir
    /// (<c>&lt;&gt;|"*?</c> ou un caractere de controle). Un chemin vide n'est PAS interdit : il ne designe rien,
    /// mais il ne fait pas planter emulatorLauncher et il n'est pas a nous de le retirer.
    /// </summary>
    public static bool CheminInterdit(string? chemin)
    {
        var c = chemin?.Trim();
        if (string.IsNullOrEmpty(c))
        {
            return false;
        }

        foreach (var x in c)
        {
            if (x < ' ' || Array.IndexOf(Interdits, x) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Vrai pour un nom entre chevrons, comme le pseudo-jeu d'une liste vide d'ES.</summary>
    public static bool NomDePseudoJeu(string? nom)
    {
        var n = nom?.Trim();
        return !string.IsNullOrEmpty(n) && n.Length >= 2 && n[0] == '<' && n[^1] == '>';
    }

    /// <summary>
    /// Entree a retirer d'une gamelist : son chemin porte un caractere interdit (c'est le cas du pseudo-jeu
    /// d'ES, « ./&lt;Aucune entree trouvee&gt; »). Jugee par le CHEMIN SEUL : un vrai jeu dont le nom serait entre
    /// chevrons, ou dont le chemin est entoure de blancs, n'est jamais retire (relecture de la 1.9.210).
    /// </summary>
    public static bool EntreeInvalide(XElement game) => CheminInterdit(game.Element("path")?.Value);

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
