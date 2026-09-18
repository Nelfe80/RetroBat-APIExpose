using RetroBat.Domain.Services;
using Xunit;
using static RetroBat.Domain.Services.EmulationStationScraperVocabulary;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les clés natives d'ES (ScrapperImageSrc, ScrapperLogoSrc, ScrapperThumbSrc) reçoivent le
/// vocabulaire d'ES, pas celui d'APIExpose : recopiés tels quels, box2d et logo faisaient
/// afficher NONE à son menu (testeur, 2026-09-17).
/// </summary>
public class EmulationStationScraperVocabularyTests
{
    [Theory]
    [InlineData(Slot.Image, "sstitle", "sstitle")]
    [InlineData(Slot.Image, "mix", "mixrbv2")]
    [InlineData(Slot.Image, "box2d", "box-2D")]
    [InlineData(Slot.Logo, "logo", "wheel")]
    [InlineData(Slot.Logo, "wheel-hd", "wheel-hd")]
    [InlineData(Slot.Logo, "marquee", "marquee")]
    [InlineData(Slot.Thumb, "box2d", "box-2D")]
    [InlineData(Slot.Thumb, "BOX3D", "box-3D")]
    public void ToEmulationStation_TraduitLesChoixQuEsConnait(Slot slot, string apiExpose, string attendu)
    {
        Assert.Equal(attendu, ToEmulationStation(slot, apiExpose));
    }

    [Theory]
    [InlineData(Slot.Logo, "screenmarquee")]
    [InlineData(Slot.Logo, "figurine")]
    [InlineData(Slot.Thumb, "ss")]
    [InlineData(Slot.Thumb, "cartridge")]
    [InlineData(Slot.Image, "")]
    [InlineData(Slot.Image, null)]
    public void ToEmulationStation_SansEquivalent_LaisseLaCleDEsTelleQuelle(Slot slot, string? apiExpose)
    {
        Assert.Null(ToEmulationStation(slot, apiExpose));
    }

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
