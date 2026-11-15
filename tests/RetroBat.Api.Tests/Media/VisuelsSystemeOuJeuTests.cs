using System.Text.Json;
using RetroBat.Api.Infrastructure;
using Xunit;
using P = RetroBat.Api.Infrastructure.PhysicalMediaWebSocketProjectionService;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// BUG DU 15/11 : des visuels du SYSTEME s'affichaient a la place de ceux du JEU, et le DMD du systeme fbneo montrait MAME.
/// 1. Le DMD genere d'un systeme replie sous "arcade" etait un seul fichier, generated-system-dmd.png, ecrit par le dernier
///    frontal genere ; il est desormais par frontal, comme le marquee genere.
/// 2. Dans un instantane de jeu, Media.* replie sur le systeme sans le dire : chaque media porte maintenant Scope.
/// </summary>
public class VisuelsSystemeOuJeuTests
{
    private static P.MediaStreamAsset Asset(string nom) => new(
        Kind: "x", Origin: "apiexpose", Path: nom, FileName: nom,
        Stem: System.IO.Path.GetFileNameWithoutExtension(nom), Extension: ".png",
        Length: 0, LastWriteTimeUtc: DateTime.MinValue, Url: "/" + nom);

    private static P.MarqueeMediaSnapshot Media(
        string? marquee = null, string? generated = null, string? dmdGenerated = null,
        string? fanart = null, string? logo = null, string? video = null)
        => new(
            Marquee: marquee is null ? null : Asset(marquee),
            GeneratedMarquee: generated is null ? null : Asset(generated),
            ScreenMarquee: null,
            ScreenMarqueeSmall: null,
            Dmd: new P.DmdMediaSnapshot("dmd", null, dmdGenerated is null ? null : Asset(dmdGenerated), []),
            Topper: null,
            Fanart: fanart is null ? null : Asset(fanart),
            Logo: logo is null ? null : Asset(logo),
            Video: video is null ? null : Asset(video));

    [Fact]
    public void Le_dmd_genere_d_un_systeme_replie_est_par_frontal()
    {
        using var tree = new MediaTree();
        tree.With("arcade/artwork/marquee/generated-system-dmd.png")
            .With("arcade/artwork/marquee/generated-system-dmd.mame.png");
        var roots = new[] { Path.Combine(tree.Root, "arcade") };

        Assert.Equal("generated-system-dmd.mame.png", Path.GetFileName(P.ResolveSystemGeneratedDmdAsset("mame", "arcade", roots)!.Path));
        // fbneo n'a pas encore le sien : jamais celui de mame, ni le fichier partage de l'agregat.
        Assert.Null(P.ResolveSystemGeneratedDmdAsset("fbneo", "arcade", roots));
        // L'agregat "arcade" garde le nom simple, et n'attrape pas un voisin par ordre alphabetique.
        Assert.Equal("generated-system-dmd.png", Path.GetFileName(P.ResolveSystemGeneratedDmdAsset("arcade", "arcade", roots)!.Path));
    }

    [Fact]
    public void Un_systeme_un_pour_un_garde_son_ancien_dmd_genere()
    {
        using var tree = new MediaTree();
        tree.With("snes/artwork/marquee/generated-dmd.png");
        var roots = new[] { Path.Combine(tree.Root, "snes") };

        Assert.Equal("generated-dmd.png", Path.GetFileName(P.ResolveSystemGeneratedDmdAsset("snes", "snes", roots)!.Path));

        tree.With("snes/artwork/marquee/generated-system-dmd.png");
        Assert.Equal("generated-system-dmd.png", Path.GetFileName(P.ResolveSystemGeneratedDmdAsset("snes", "snes", roots)!.Path));
    }

    [Fact]
    public void Un_media_replie_sur_le_systeme_le_dit()
    {
        var jeu = Media(marquee: "games/19xx/marquee.png", fanart: "games/19xx/fanart.jpg", video: "games/19xx/video.mp4");
        var systeme = Media(marquee: "sys-marquee.png", generated: "generated-system-marquee.fbneo.png",
            dmdGenerated: "generated-system-dmd.fbneo.png", fanart: "sys-fanart.jpg", logo: "wheel.fbneo.png");

        var media = P.MergeGameOverSystem(jeu, systeme);

        Assert.Equal("game", media.Marquee!.Scope);
        Assert.Equal("games/19xx/marquee.png", media.Marquee.Path);
        Assert.Equal("system", media.GeneratedMarquee!.Scope);
        Assert.Equal("generated-system-marquee.fbneo.png", media.GeneratedMarquee.Path);
        Assert.Equal("game", media.Fanart!.Scope);
        Assert.Equal("system", media.Logo!.Scope);
        Assert.Equal("game", media.Video!.Scope);
        var dmd = (P.DmdMediaSnapshot)media.Dmd;
        Assert.Equal("system", dmd.Scope);
        Assert.Equal("system", dmd.Generated!.Scope);
        Assert.Null(media.Topper);
    }

    [Fact]
    public void Le_dmd_du_jeu_est_pris_en_entier_et_marque_jeu()
    {
        var jeu = Media(dmdGenerated: "games/bonk/generated-dmd.png");
        var systeme = Media(dmdGenerated: "generated-system-dmd.png");

        var dmd = (P.DmdMediaSnapshot)P.MergeGameOverSystem(jeu, systeme).Dmd;

        Assert.Equal("game", dmd.Scope);
        Assert.Equal("games/bonk/generated-dmd.png", dmd.Generated!.Path);
        Assert.Equal("game", dmd.Generated.Scope);
    }

    [Fact]
    public void Un_instantane_de_systeme_est_tout_entier_systeme()
    {
        var media = P.WithSystemScope(Media(marquee: "m.png", fanart: "f.jpg", dmdGenerated: "d.png"));

        Assert.Equal("system", media.Marquee!.Scope);
        Assert.Equal("system", media.Fanart!.Scope);
        Assert.Equal("system", ((P.DmdMediaSnapshot)media.Dmd).Scope);
        Assert.Null(media.Logo);
    }

    [Fact]
    public void Le_champ_est_additif_et_omis_sans_portee()
    {
        var sans = JsonSerializer.Serialize(Asset("a.png"));
        Assert.DoesNotContain("Scope", sans);

        var avec = JsonSerializer.Serialize(P.WithMediaScope(Asset("a.png"), "system"));
        Assert.Contains("\"Scope\":\"system\"", avec);
        Assert.Contains("\"Path\":\"a.png\"", avec);

        // Dmd est publie en object : le serialiseur suit le type reel, la portee du bloc part avec.
        object bloc = P.WithMediaScope(new P.DmdMediaSnapshot("dmd", null, null, []), "game");
        Assert.Contains("\"Scope\":\"game\"", JsonSerializer.Serialize(bloc));
    }
}
