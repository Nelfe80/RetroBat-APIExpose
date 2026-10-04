using Xunit;
using static RetroBat.Domain.Services.EmulationStationScraperVocabulary;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les choix du scraper intégré d'ES, repris dans le vocabulaire d'APIExpose quand APIExpose n'a
/// pas encore les siens. APIExpose lit ces clés natives et ne les écrit jamais (règle du
/// 2026-10-04 : elles sont au joueur).
/// </summary>
public class EmulationStationScraperVocabularyTests
{
    [Theory]
    [InlineData("box-2D", "box2d")]
    [InlineData("box-3D", "box3d")]
    [InlineData("wheel", "logo")]
    [InlineData("wheel-hd", "wheel-hd")]
    [InlineData("mixrbv2", "mix")]
    [InlineData("sstitle", "sstitle")]
    public void FromEmulationStation_RepriseDesChoixDUneInstallationSansApiExpose(string es, string attendu)
    {
        Assert.Equal(attendu, FromEmulationStation(es));
    }

    [Fact]
    public void FromEmulationStation_Vide_NeDitRien()
    {
        Assert.Null(FromEmulationStation(""));
        Assert.Null(FromEmulationStation(null));
    }
}
