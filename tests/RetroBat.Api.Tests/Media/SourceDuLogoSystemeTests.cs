using RetroBat.Api.Infrastructure;
using Xunit;
using P = RetroBat.Api.Infrastructure.PhysicalMediaWebSocketProjectionService;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// LA SOURCE DU LOGO D'UN SYSTEME (brief du 13/11, point 1.5) : un logo deja mis en cache par l'API n'est jamais une source
/// (compare a lui-meme, il ne changeait plus) ; quand le logo change, le marquee et le DMD generes de l'API s'en vont.
/// </summary>
public class SourceDuLogoSystemeTests
{
    [Fact]
    public void Un_logo_en_cache_de_l_api_n_est_pas_une_source()
    {
        using var tree = new MediaTree();
        tree.With("nelfeplay-scoring/ui/wheels/wheel.png")
            .With("nelfeplay-scoring/ui/wheels/wheel.png.apiexpose-cache", "system-logo-cache-v5-png32-srgb|...|361721|0")
            .With("arcade/ui/wheels/wheel.fbneo.png")
            .With("snes/ui/wheels/wheel.png");

        Assert.True(P.IsApiLogoCache(Path.Combine(tree.Root, "nelfeplay-scoring", "ui", "wheels", "wheel.png")));
        Assert.True(P.IsApiLogoCache(Path.Combine(tree.Root, "nelfeplay-scoring", "ui", "wheels", "wheel.png.apiexpose-cache")));
        // Sans marqueur, un logo pose par l'utilisateur ou un pack reste une source.
        Assert.False(P.IsApiLogoCache(Path.Combine(tree.Root, "snes", "ui", "wheels", "wheel.png")));
        Assert.False(P.IsApiLogoCache(Path.Combine(tree.Root, "arcade", "ui", "wheels", "wheel.fbneo.png")));
    }

    [Fact]
    public void Les_medias_generes_oublies_sont_ceux_que_la_generation_ecrit()
    {
        Assert.Equal(["generated-system-marquee.png", "generated-system-dmd.png"], P.GeneratedSystemMediaNames("nelfeplay-scoring", "nelfeplay-scoring"));
        Assert.Equal(["generated-system-marquee.fbneo.png", "generated-system-dmd.png"], P.GeneratedSystemMediaNames("fbneo", "arcade"));
    }
}
