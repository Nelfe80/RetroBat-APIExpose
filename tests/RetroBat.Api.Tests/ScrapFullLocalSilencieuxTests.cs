using RetroBat.Api.Media;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un scrap « FL » (tous les medias du jeu deja locaux, seuls les textes cherches) ne notifie plus
/// rien : des joueurs voyaient « Scraping (FL) : 1942 » a chaque lancement de RetroBat (regle user
/// du 2026-10-04). Les autres modes gardent leurs notifications.
/// </summary>
public class ScrapFullLocalSilencieuxTests
{
    [Theory]
    [InlineData("FL", true)]
    [InlineData("fl", true)]
    [InlineData("H", false)]
    [InlineData("FD", false)]
    [InlineData("", false)]
    public void Seul_le_mode_FL_se_tait(string mode, bool silencieux)
    {
        Assert.Equal(silencieux, RemoteScrapingService.EstSilencieux(new RemoteScrapeDecision { WorkflowMode = mode }));
    }
}
