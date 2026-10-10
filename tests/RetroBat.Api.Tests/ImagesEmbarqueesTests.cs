using System.Drawing;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les images que le code dessine voyagent dans l'exe (2026-10-10). Lues dans media/, un dossier que ni la mise a jour
/// ni l'installeur ne livrent, elles n'existaient que sur la borne qui fabrique la release. La planche des reactions est
/// eprouvee dans <see cref="ReplayBarreTests"/>.
/// </summary>
public class ImagesEmbarqueesTests
{
    [Theory]
    [InlineData("livecontest-icon.png", 32)]
    [InlineData("livecontest-icon-big.png", 128)]
    public void Les_icones_du_concours_en_direct_sont_dans_l_exe(string nom, int cote)
    {
        using var image = LiveContestOverlayService.IconeEmbarquee(nom);
        Assert.NotNull(image);
        Assert.Equal(new Size(cote, cote), image!.Size);
    }

    [Fact]
    public void Une_icone_absente_rend_null_sans_lever()
    {
        Assert.Null(LiveContestOverlayService.IconeEmbarquee("absente.png"));
    }
}
