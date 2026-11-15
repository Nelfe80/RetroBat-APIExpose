using System.Text.Json;
using RetroBat.Api.Leaderboard;
using Xunit;

namespace RetroBat.Api.Tests;

public sealed class ClassementAuVerdictTests
{
    [Fact]
    public void Un_score_publie_relit_le_classement()
    {
        Assert.True(LeaderboardInputService.VerdictPublie(new { SessionId = "s", Status = "published", Score = 1200L }));
        Assert.True(LeaderboardInputService.VerdictPublie(JsonSerializer.SerializeToElement(new { status = "PUBLISHED" })));
    }

    [Fact]
    public void Un_score_refuse_en_attente_ou_un_message_vide_ne_relit_rien()
    {
        Assert.False(LeaderboardInputService.VerdictPublie(new { Status = "quarantined" }));
        Assert.False(LeaderboardInputService.VerdictPublie(new { Status = "refused" }));
        Assert.False(LeaderboardInputService.VerdictPublie(new { Score = 3 }));
        Assert.False(LeaderboardInputService.VerdictPublie(null));
        Assert.False(LeaderboardInputService.VerdictPublie("texte"));
    }
}
