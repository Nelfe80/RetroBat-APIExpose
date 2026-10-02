using RetroBat.Api.Leaderboard;
using Xunit;
using Mode = RetroBat.Api.Leaderboard.LeaderboardInputService.ModeDuDefi;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le bouton CHALLENGE dit le defi qu'il lance, d'apres les reglages du joueur (2026-10-02) : 1CC
/// prive, 1CC LIVE, 1CC MULTI. Le MULTI n'existe que sur un jeu qui a ce classement ; l'onglet
/// LIVE & CONTEST lance toujours en direct.
/// </summary>
public sealed class LeaderboardChallengeModeTests
{
    [Theory]
    [InlineData(false, false, "everyone", true, "Prive")]
    [InlineData(false, true, "none", true, "Live")]
    [InlineData(false, true, "followed", true, "Multi")]
    [InlineData(false, true, "everyone", true, "Multi")]
    [InlineData(false, true, "everyone", false, "Live")]   // pas de classement 1CC MULTI
    [InlineData(true, false, "none", true, "Live")]        // LIVE & CONTEST : toujours en direct
    [InlineData(true, false, "followed", true, "Multi")]
    [InlineData(true, false, "followed", false, "Live")]
    public void Le_mode_suit_les_reglages(bool surLeLive, bool partage, string politique, bool jeuMulti, string attendu)
        => Assert.Equal(attendu, LeaderboardInputService.ModeDuDefiPour(surLeLive, partage, politique, jeuMulti).ToString());

    [Fact]
    public void Le_libelle_suit_la_regle_du_jeu()
    {
        Assert.Equal("1CC", LeaderboardInputService.LibelleDuMode(Mode.Prive, new[] { "1cc" }));
        Assert.Equal("1CC LIVE", LeaderboardInputService.LibelleDuMode(Mode.Live, new[] { "1cc", "1cc-multi" }));
        Assert.Equal("1CC MULTI", LeaderboardInputService.LibelleDuMode(Mode.Multi, new[] { "1cc", "1cc-multi" }));
        Assert.Equal("1LC LIVE", LeaderboardInputService.LibelleDuMode(Mode.Live, new[] { "1lc" }));
        Assert.Equal("1CC", LeaderboardInputService.LibelleDuMode(Mode.Prive, System.Array.Empty<string>()));
    }
}
