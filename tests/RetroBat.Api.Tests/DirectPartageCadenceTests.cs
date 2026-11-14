using System.Text.Json.Nodes;
using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Lot 4 bis C-5 (14/11) : <c>agent/live/shared-poll</c>, 4 031 appels par jour, 10 s pendant toute partie. Le site pilote la
/// cadence (<c>poll_after</c>) ; sans elle, 30 s ; site muet, 60 s.
/// </summary>
public sealed class DirectPartageCadenceTests
{
    [Fact]
    public void La_cadence_du_site_l_emporte_dans_ses_bornes()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), DirectPartageService.AttenteDuPartage(60, 0));
        Assert.Equal(TimeSpan.FromSeconds(3), DirectPartageService.AttenteDuPartage(1, 0));
        Assert.Equal(TimeSpan.FromSeconds(60), DirectPartageService.AttenteDuPartage(600, 0));
        Assert.Equal(TimeSpan.FromSeconds(10), DirectPartageService.AttenteDuPartage(10, 3));
    }

    [Fact]
    public void Sans_cadence_du_site_trente_secondes_puis_soixante_si_le_site_se_tait()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), DirectPartageService.AttenteDuPartage(null, 0));
        Assert.Equal(TimeSpan.FromSeconds(30), DirectPartageService.AttenteDuPartage(null, 1));
        Assert.Equal(TimeSpan.FromSeconds(60), DirectPartageService.AttenteDuPartage(null, 2));
    }

    [Fact]
    public void Le_poll_after_se_lit_entier_ou_decimal()
    {
        Assert.Equal(10, DirectPartageService.PollAfter(JsonNode.Parse("{\"poll_after\":10}") as JsonObject));
        Assert.Equal(60, DirectPartageService.PollAfter(JsonNode.Parse("{\"poll_after\":59.6}") as JsonObject));
        Assert.Null(DirectPartageService.PollAfter(JsonNode.Parse("{\"role\":\"wait\"}") as JsonObject));
        Assert.Null(DirectPartageService.PollAfter(JsonNode.Parse("{\"poll_after\":\"x\"}") as JsonObject));
        Assert.Null(DirectPartageService.PollAfter(null));
    }
}
