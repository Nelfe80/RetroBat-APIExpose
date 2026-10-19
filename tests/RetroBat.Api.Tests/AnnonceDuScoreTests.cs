using RetroBat.Api.Infrastructure;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'annonce de fin de partie nomme le classement de son rang (decision user 2026-10-20) : un joueur lisait un rang de
/// Tetris TGM2 sans savoir qu'il parlait du 1CC NORMAL.
/// </summary>
public class AnnonceDuScoreTests
{
    [Theory]
    [InlineData("1cc", "1CC")]
    [InlineData("1cc-normal", "1CC NORMAL")]
    [InlineData("1lc-super", "1LC SUPER")]
    [InlineData(" 1cc-multi ", "1CC MULTI")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Le_nom_du_classement_s_ecrit_comme_sur_le_site(string? regle, string attendu)
        => Assert.Equal(attendu, ModesDeJeu.NomDuClassement(regle));

    [Fact]
    public void Le_rang_dit_son_classement()
    {
        var texte = NelfePlayScoringReporter.TexteDuScorePublie(27186, 6, "1cc-normal");
        Assert.StartsWith("Score certifié : ", texte);
        Assert.EndsWith(" publié (#6 en 1CC NORMAL)", texte);
    }

    [Fact]
    public void Sans_rang_le_classement_reste_nomme()
        => Assert.EndsWith(" publié (1LC)", NelfePlayScoringReporter.TexteDuScorePublie(52010, null, "1lc"));

    [Fact]
    public void Sans_regle_le_rang_seul()
        => Assert.EndsWith(" publié (#3)", NelfePlayScoringReporter.TexteDuScorePublie(1000, 3, null));
}
