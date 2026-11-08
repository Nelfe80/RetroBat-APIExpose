using RetroBat.Api.Replay.Video;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// UN JEU VERTICAL FILME COUCHE (08/11, Contra de Getupor sur YouTube) : le film d'un jeu vertical ne part que si son image,
/// tournee comme le coeur l'annonce, est en portrait.
/// </summary>
public class CaptureJeuVerticalTests
{
    [Theory]
    [InlineData(256, 224, 1, true)]   // image du coeur en paysage, un quart de tour : portrait
    [InlineData(256, 224, 3, true)]
    [InlineData(224, 256, 0, true)]   // le coeur la tourne deja : portrait
    [InlineData(256, 224, 0, false)]  // le cas de Contra : paysage, aucune rotation annoncee
    [InlineData(224, 256, 1, false)]  // deja tournee, tournee encore : couchee
    [InlineData(256, 224, null, false)] // rotation inconnue : on ne devine pas
    [InlineData(240, 240, 1, false)]  // carree : rien a dire
    public void Le_film_d_un_jeu_vertical_doit_etre_en_portrait_une_fois_tourne(long largeur, long hauteur, int? rotation, bool attendu)
        => Assert.Equal(attendu, ReplayCaptureService.PortraitUneFoisTourne(largeur, hauteur, rotation));

    [Fact]
    public void Sans_chemin_le_jeu_n_est_pas_dit_vertical()
    {
        Assert.False(ReplayCaptureService.JeuVertical(null));
        Assert.False(ReplayCaptureService.JeuVertical(""));
    }
}
