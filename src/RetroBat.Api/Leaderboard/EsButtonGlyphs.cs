using System.Runtime.Versioning;

namespace RetroBat.Api.Leaderboard;

/// <summary>
/// Les pictogrammes de touches d'EmulationStation, pour TOUS les cartouches d'APIExpose.
///
/// Le joueur apprend ses touches dans la barre d'aide d'ES. Si le panneau de classement, la
/// legende des reactions et la barre du replay dessinent chacun leurs propres touches (lettres,
/// croix vectorielle, badge maison), il doit en apprendre trois. Ici, une seule source : le jeu
/// d'icones d'aide ACTIF du theme (voir <see cref="EsMenuStyle.HelpIcon"/>), converti une fois et
/// garde dans media/leaderboard.
///
/// Rien ne bloque le dessin : tant qu'une image n'est pas prete, <see cref="Touche"/> rend null et
/// l'appelant garde son ancien dessin ; la conversion se fait en arriere-plan.
/// </summary>
[SupportedOSPlatform("windows")]
public static class EsButtonGlyphs
{
    private static readonly object Verrou = new();
    private static EsGlyphCache? _cache;
    private static EsMenuStyle? _style;

    private static EsGlyphCache Cache()
    {
        lock (Verrou)
        {
            var style = EsMenuStyle.Lire();
            if (_cache is null || !ReferenceEquals(style, _style))
            {
                _cache = new EsGlyphCache(style);
                _style = style;
            }
            return _cache;
        }
    }

    /// <summary>La couleur des pictogrammes de la barre d'aide d'ES (iconColor du theme).</summary>
    public static Color Teinte()
    {
        var (r, v, b, _) = EsMenuStyle.Couleur(EsMenuStyle.Lire().HelpIconColor);
        return Color.FromArgb(255, r, v, b);
    }

    /// <summary>Le pictogramme d'une identite de bouton (a, b, x, y, l, r, l2, r2, start, select).</summary>
    public static Image? Touche(string identite, int hauteur, Color? teinte = null)
    {
        try
        {
            var chemin = EsMenuStyle.Lire().HelpIcon(identite);
            return chemin.Length == 0 ? null : Cache().Glyphe(chemin, hauteur, teinte ?? Teinte());
        }
        catch (Exception) { return null; }
    }

    /// <summary>Une image dans SES couleurs (le sceau certifie garde son orange), sans teinte.</summary>
    public static Image? TelQuel(string nom, int hauteur)
    {
        try { return Cache().Glyphe(nom, hauteur); }
        catch (Exception) { return null; }
    }

    /// <summary>Une icone d'aide par son nom (dpad_up, dpad_leftright, dpad_down, dpad_updown…).</summary>
    public static Image? Aide(string nom, int hauteur, Color? teinte = null)
    {
        try { return Cache().Glyphe(nom, hauteur, teinte ?? Teinte()); }
        catch (Exception) { return null; }
    }
}
