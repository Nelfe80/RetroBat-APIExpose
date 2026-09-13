using RetroBat.Api.Leaderboard;
using RetroBat.Domain.Paths;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La charte d'EmulationStation, lue sur le disque. Si cette lecture derive, le panneau de
/// classement se voit comme une piece rapportee a cote du menu natif - et c'est tout ce qu'on
/// cherche a eviter.
/// </summary>
public class EsThemeStyleTests
{
    [Theory]
    [InlineData("7d7d7d", 0x7d, 0x7d, 0x7d, 255)]
    [InlineData("#3675CA", 0x36, 0x75, 0xCA, 255)]
    [InlineData("4075D8C5", 0x40, 0x75, 0xD8, 0xC5)]   // les themes ecrivent l'alpha en quatrieme
    [InlineData("051222", 0x05, 0x12, 0x22, 255)]
    public void Une_couleur_de_theme_se_lit_avec_son_alpha(string hex, int r, int g, int b, int a)
        => Assert.Equal((r, g, b, a), EsThemeStyle.Couleur(hex));

    [Theory]
    [InlineData("")]
    [InlineData("bleu")]
    [InlineData("12345")]
    [InlineData("zzzzzz")]
    public void Une_couleur_illisible_ne_fait_pas_tomber_le_panneau(string hex)
    {
        var (r, g, b, a) = EsThemeStyle.Couleur(hex);
        Assert.Equal((255, 255, 255, 255), (r, g, b, a));
    }

    [Fact]
    public void Un_theme_absent_rend_une_charte_de_repli_utilisable()
    {
        var style = EsThemeStyle.Lire(Path.Combine(Path.GetTempPath(), "pas-d-emulationstation-ici"), "theme-fantome");
        Assert.Equal("theme-fantome", style.ThemeSet);
        Assert.Equal("", style.FontPath);           // pas de police : l'appelant prendra la sienne
        Assert.Equal("", style.Icon("dpad_left"));  // pas de glyphe : il s'en passera
        Assert.True(style.HelpFontSize > 0);        // mais les valeurs de repli restent utilisables
        Assert.NotEqual("", style.BackgroundColor);
    }

    /// <summary>
    /// Sur une vraie borne : la police, les couleurs et les glyphes doivent SE TROUVER. Ce test
    /// ne s'execute que la ou EmulationStation est installe ; ailleurs il n'a rien a dire.
    /// </summary>
    [Fact]
    public void Sur_une_borne_la_charte_se_lit_vraiment()
    {
        var racine = Path.Combine(RetroBatPaths.RetroBatRoot, "emulationstation");
        if (!Directory.Exists(Path.Combine(racine, "resources", "help")))
        {
            return;   // pas d'EmulationStation ici : rien a verifier
        }

        var style = EsThemeStyle.Lire(racine, EsThemeStyle.LireThemeSet());
        Assert.NotEqual("", style.ThemeSet);
        // Les glyphes de boutons : ce sont EUX que le panneau affichera, pas des dessins maison.
        Assert.NotEqual("", style.Icon("dpad_left"));
        Assert.NotEqual("", style.Icon("dpad_right"));
        Assert.NotEqual("", style.Icon("button_b"));
        Assert.EndsWith(".svg", style.Icon("dpad_updown"));
        // Un glyphe qui n'existe pas rend une chaine vide, il ne jette pas.
        Assert.Equal("", style.Icon("bouton_imaginaire"));
        // La police du theme, quand il en declare une, doit exister sur le disque.
        if (style.FontPath.Length > 0) Assert.True(File.Exists(style.FontPath));
        if (style.FontBoldPath.Length > 0) Assert.True(File.Exists(style.FontBoldPath));
        Assert.InRange(style.HelpFontSize, 0.005, 0.2);
    }
}
