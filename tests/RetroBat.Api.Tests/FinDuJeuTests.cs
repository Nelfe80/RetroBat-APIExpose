using System.Text.Json;
using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Le ✓1CC (One-Credit Clear, 24/10) : la regle du profil, le signal du .MEM, le run retenu.</summary>
public sealed class FinDuJeuTests
{
    private static JsonElement Profil(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // Sonic 1 : LEVEL_ENDING a 0xFE11 = 6, la zone de la fin (le .MEM a d'autres lignes de la meme action).
    private static readonly FinDuJeu.Regle Sonic = new("LEVEL_ENDING", 0xFE11, 6);

    [Fact]
    public void La_regle_se_lit_dans_le_profil_avec_ou_sans_adresse_ni_valeur()
    {
        Assert.Equal(Sonic, FinDuJeu.DuProfil(Profil("""{"clear":{"action":"level_ending","address":"0xFE11","value":6}}""")));
        Assert.Equal(Sonic, FinDuJeu.DuProfil(Profil("""{"clear":{"action":"LEVEL_ENDING","address":"FE11","value":"0x06"}}""")));
        Assert.Equal(new FinDuJeu.Regle("CREDITS_SCREEN", null, null), FinDuJeu.DuProfil(Profil("""{"clear":{"action":"CREDITS_SCREEN"}}""")));
        Assert.Null(FinDuJeu.DuProfil(Profil("""{"ruleset":"1cc"}""")));
        Assert.Null(FinDuJeu.DuProfil(Profil("""{"clear":{"address":"0xFE11"}}""")));
        Assert.Null(FinDuJeu.DuProfil(Profil("""{"clear":{"action":"LEVEL_ENDING","address":"zone"}}""")));
        Assert.Null(FinDuJeu.DuProfil(null));
    }

    [Theory]
    [InlineData("LEVEL_ENDING", "0x00FE11", 6, true)]
    [InlineData("level_ending", "0xFE11", 6, true)]
    [InlineData("LEVEL_ENDING", "0x00FE11", 5, false)]
    [InlineData("LEVEL_ENDING", "0x00FE10", 6, false)]
    [InlineData("CREDITS_SCREEN", "0x00FE11", 6, false)]
    [InlineData("LEVEL_ENDING", null, 6, false)]
    public void Seul_le_signal_de_la_regle_compte(string nom, string? adresse, int valeur, bool attendu)
        => Assert.Equal(attendu, FinDuJeu.Correspond(Sonic, nom, adresse, valeur));

    [Fact]
    public void Une_regle_sans_adresse_prend_le_signal_du_pont_MAME_qui_n_en_donne_pas()
        => Assert.True(FinDuJeu.Correspond(new FinDuJeu.Regle("CREDITS_SCREEN", null, 0x1C), "CREDITS_SCREEN", null, 0x1C));

    [Fact]
    public void Le_clear_appartient_au_run_qu_il_suit_et_pas_a_une_autre_partie()
    {
        var run = new List<(long frame, long total)> { (1000, 100), (5000, 9000), (20000, 45000) };
        var lectures = new List<(long frame, long total)>(run);
        Assert.True(FinDuJeu.DansLeRun(new FinDuJeu.Clear(21000, 45000, Sonic), run, lectures));
        Assert.True(FinDuJeu.DansLeRun(new FinDuJeu.Clear(10000, 20000, Sonic), run, lectures));
        // Avant le run, ou longtemps apres : une autre partie.
        Assert.False(FinDuJeu.DansLeRun(new FinDuJeu.Clear(500, 0, Sonic), run, lectures));
        Assert.False(FinDuJeu.DansLeRun(new FinDuJeu.Clear(20000 + FinDuJeu.FramesApresLeRun + 1, 45000, Sonic), run, lectures));
        // Une nouvelle partie (le score retombe) entre la fin du run et le clear.
        var relance = new List<(long frame, long total)>(run) { (20500, 0), (20800, 300) };
        Assert.False(FinDuJeu.DansLeRun(new FinDuJeu.Clear(21000, 300, Sonic), run, relance));
        Assert.False(FinDuJeu.DansLeRun(new FinDuJeu.Clear(21000, 0, Sonic), [], lectures));
    }

    [Fact]
    public void Le_clear_garde_dans_le_brouillon_se_relit_a_l_identique()
    {
        var clear = new FinDuJeu.Clear(21000, 45000, Sonic);
        var json = FinDuJeu.EnJson(clear);
        Assert.Equal("0xFE11", (string?) json["address"]);
        Assert.Equal("45000", (string?) json["score"]);
        Assert.Equal(clear, FinDuJeu.DuJson(json));
        Assert.Equal(new FinDuJeu.Clear(7, 0, new FinDuJeu.Regle("CREDITS_SCREEN", null, null)),
            FinDuJeu.DuJson(FinDuJeu.EnJson(new FinDuJeu.Clear(7, 0, new FinDuJeu.Regle("CREDITS_SCREEN", null, null)))));
        Assert.Null(FinDuJeu.DuJson(null));
    }
}
