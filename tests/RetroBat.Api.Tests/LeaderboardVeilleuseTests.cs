using RetroBat.Api.Leaderboard;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La veilleuse au-dessus d'ES (30/10/2026) : elle evite le flash du panneau, et ne doit jamais rester pendant une partie,
/// ou le jeu passerait par le compositeur de Windows, avec une image de retard.
/// </summary>
public class LeaderboardVeilleuseTests
{
    [Theory]
    [InlineData("browsing", true)]
    [InlineData("Browsing", true)]
    [InlineData("playing", false)]
    [InlineData("launching", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Elle_ne_se_pose_qu_en_navigation(string? etat, bool attendu)
        => Assert.Equal(attendu, LeaderboardOverlayService.VeilleuseVoulue(true, etat, esPresent: true, replayEnLecture: false));

    [Fact]
    public void Sans_ES_pas_de_veilleuse()
        => Assert.False(LeaderboardOverlayService.VeilleuseVoulue(true, "browsing", esPresent: false, replayEnLecture: false));

    [Fact]
    public void Pendant_un_replay_pas_de_veilleuse()
        => Assert.False(LeaderboardOverlayService.VeilleuseVoulue(true, "browsing", esPresent: true, replayEnLecture: true));

    [Fact]
    public void Le_reglage_la_coupe()
        => Assert.False(LeaderboardOverlayService.VeilleuseVoulue(false, "browsing", esPresent: true, replayEnLecture: false));
}
