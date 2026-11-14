using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// LA FIN D'UNE PARTIE RETROACHIEVEMENTS (brief du 13/11, remarque du point 1.4) : le mode hardcore ne reste pas vrai sans
/// jeu en cours dans /api/retroachievements/status.
/// </summary>
public class RetroAchievementsFinDePartieTests
{
    [Fact]
    public void La_fin_de_partie_efface_le_jeu_et_le_mode_hardcore_mais_garde_le_joueur()
    {
        var session = new RetroAchievementsSessionSnapshot
        {
            Username = "Nelfe",
            GameId = 1234,
            GameTitle = "Shinobi",
            GameHash = "0123456789abcdef0123456789abcdef",
            Hardcore = true,
            Score = 250,
            AchievementCount = 40,
        };
        session.SessionUnlockedAchievements.Add(7);

        RetroAchievementsService.OublierLaPartie(session);

        Assert.False(session.Hardcore);
        Assert.Null(session.GameId);
        Assert.Equal("", session.GameTitle);
        Assert.Null(session.Score);
        Assert.Empty(session.SessionUnlockedAchievements);
        Assert.Equal("Nelfe", session.Username);
    }
}
