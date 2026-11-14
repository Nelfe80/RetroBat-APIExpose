using Microsoft.Extensions.Logging.Abstractions;
using RetroBat.Api.Leaderboard;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Brief du 13/11, ECR-09 : en speedrun, le filet « 45 s sans geste » rendait la main a ES au moment ou le jeu arrivait,
/// parce que la page de preparation, tenue devant le jeu, ne comptait pas comme un geste.
/// </summary>
public class FiletDuSilenceTests
{
    [Fact]
    public void Page_tenue_devant_le_jeu_plus_de_45_s_le_filet_ne_tombe_pas()
    {
        var overlay = new LeaderboardOverlayService(NullLogger<LeaderboardOverlayService>.Instance);
        overlay.SimulerUnePageTenue(DateTime.UtcNow - LeaderboardOverlayService.SilenceMax - TimeSpan.FromSeconds(5));
        overlay.GarderDevantLeJeu();   // ce que font les pages de depart toutes les 500 ms
        Assert.False(overlay.TropLongSilence());
    }

    [Fact]
    public void Sans_page_tenue_le_filet_tombe_toujours_apres_45_s()
    {
        var overlay = new LeaderboardOverlayService(NullLogger<LeaderboardOverlayService>.Instance);
        overlay.SimulerUnePageTenue(DateTime.UtcNow - LeaderboardOverlayService.SilenceMax - TimeSpan.FromSeconds(5));
        Assert.True(overlay.TropLongSilence());
    }
}

/// <summary>Brief du 13/11, ECR-02 : « CERTIFIABLE » n'est plus dit deux fois, ni en bandeau ni a la plaque du Deck.</summary>
public class CertifiableDitUneFoisTests
{
    [Theory]
    [InlineData("scoring_certifiable", true, true)]
    [InlineData("scoring_offline_certifiable", true, true)]
    [InlineData("scoring_certifiable", false, false)]      // page partie : la plaque du Deck ou le bandeau le dit
    [InlineData("scoring_not_certifiable", true, false)]   // une raison de refus se dit toujours
    [InlineData(null, true, false)]
    public void La_certification_se_tait_seulement_devant_la_page_de_depart(string? kind, bool page, bool tait)
        => Assert.Equal(tait, RetroBat.Api.Infrastructure.LiveContestOverlayService.TaireLaBonneNouvelle(kind, page));
}
