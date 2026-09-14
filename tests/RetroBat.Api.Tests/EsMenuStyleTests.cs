using RetroBat.Api.Leaderboard;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La charte des MENUS d'EmulationStation, lue dans un theme. Si cette lecture derive, le
/// panneau de classement se voit comme une piece rapportee a cote du menu natif - et c'est tout
/// ce qu'on cherche a eviter. Le theme d'essai reprend la structure de carbon : un colorset qui
/// declare les variables, une vue menu qui s'y refere par ${...}.
/// </summary>
public class EsMenuStyleTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "es-menu-style-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("969696", 0x96, 0x96, 0x96, 255)]
    [InlineData("#3675CA", 0x36, 0x75, 0xCA, 255)]
    [InlineData("FFFFFF08", 0xFF, 0xFF, 0xFF, 0x08)]   // les themes ecrivent l'alpha en quatrieme
    [InlineData("00000000", 0, 0, 0, 0)]                // un separateur transparent : rien a dessiner
    public void Une_couleur_de_theme_se_lit_avec_son_alpha(string hex, int r, int g, int b, int a)
        => Assert.Equal((r, g, b, a), EsMenuStyle.Couleur(hex));

    [Theory]
    [InlineData("")]
    [InlineData("bleu")]
    [InlineData("12345")]
    [InlineData("zzzzzz")]
    public void Une_couleur_illisible_ne_fait_pas_tomber_le_panneau(string hex)
        => Assert.Equal((255, 255, 255, 255), EsMenuStyle.Couleur(hex));

    [Fact]
    public void Un_theme_absent_rend_la_charte_de_repli_de_la_source_d_ES()
    {
        var style = EsMenuStyle.Lire(Path.Combine(_racine, "nulle-part"), "theme-fantome");
        Assert.Equal("theme-fantome", style.ThemeSet);
        Assert.Equal("", style.TextFontPath);           // pas de police : l'appelant prendra la sienne
        Assert.Equal("", style.Icon("dpad_left"));      // pas de glyphe : il s'en passera
        Assert.Equal(0.026, style.TextFontSize);        // les fractions d'ecran de ThemeData::ThemeMenu
        Assert.Equal("242424", style.BackgroundColor);
    }

    [Fact]
    public void Le_theme_est_lu_et_ses_variables_de_colorset_resolues()
    {
        var theme = Path.Combine(_racine, ".emulationstation", "themes", "mon-theme");
        Directory.CreateDirectory(Path.Combine(theme, "views"));
        Directory.CreateDirectory(Path.Combine(theme, "subsets", "colorsets"));
        Directory.CreateDirectory(Path.Combine(theme, "art", "fonts"));
        File.WriteAllBytes(Path.Combine(theme, "art", "fonts", "Cabin-Regular.ttf"), new byte[] { 0 });
        File.WriteAllText(Path.Combine(theme, "subsets", "colorsets", "rouge.xml"), """
            <theme><variables>
              <baseColor>C0392B</baseColor>
              <gradientEndColor>3B0000</gradientEndColor>
              <groupColor>E74C3C</groupColor>
            </variables></theme>
            """);
        File.WriteAllText(Path.Combine(theme, "views", "menu.xml"), """
            <theme><view name="menu">
              <menuText name="menutitle"><fontSize>0.034</fontSize><color>FAFAFA</color></menuText>
              <menuText name="menutext">
                <fontPath>./../art/fonts/Cabin-Regular.ttf</fontPath>
                <fontSize>0.026</fontSize>
                <fontSize tinyScreen="true">0.034</fontSize>
                <color>969696</color>
                <selectorColor>${baseColor}</selectorColor>
                <selectorColorEnd>${gradientEndColor}</selectorColorEnd>
                <selectedColor>FFFFFF</selectedColor>
                <separatorColor>00000000</separatorColor>
              </menuText>
              <menuGroup name="menugroup"><fontSize>0.018</fontSize><color>${groupColor}</color></menuGroup>
              <menuBackground name="menubg"><color>242424</color></menuBackground>
              <menuGrid name="menugrid"><separatorColor>FFFFFF08</separatorColor></menuGrid>
            </view></theme>
            """);

        var style = EsMenuStyle.Lire(_racine, "mon-theme");

        Assert.EndsWith("Cabin-Regular.ttf", style.TextFontPath);
        Assert.Equal(0.026, style.TextFontSize);            // l'ecran ordinaire, pas la variante tinyScreen
        Assert.Equal("C0392B", style.SelectorColor);        // ${baseColor} resolu : le theme rouge donne un panneau rouge
        Assert.Equal("3B0000", style.SelectorColorEnd);
        Assert.Equal("E74C3C", style.GroupColor);
        Assert.Equal("00000000", style.SeparatorColor);
        Assert.Equal("FFFFFF08", style.GridSeparatorColor);
        Assert.Equal("FAFAFA", style.TitleColor);
    }
}
