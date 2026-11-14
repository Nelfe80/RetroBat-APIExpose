using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using RetroBat.Api.Media;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// API-7 (14/11), retour d'un utilisateur du 30/09 : fanart range par Skraper dans roms\&lt;systeme&gt;\media\fanart\&lt;rom&gt;.png
/// non repris, boites 3D classees en capture d'ecran. Le dossier de type dit le type ; le creneau generique de la
/// gamelist (&lt;image&gt;, &lt;thumbnail&gt;) ne le dit jamais.
/// </summary>
public sealed class DossiersDeTypeTests : IDisposable
{
    private readonly string _systeme = Path.Combine(Path.GetTempPath(), "api7-" + Guid.NewGuid().ToString("N"), "snes");
    private readonly MediaQualificationService _qual = new();

    public DossiersDeTypeTests() => Directory.CreateDirectory(_systeme);

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_systeme)!, recursive: true); } catch { /* best effort */ }
    }

    private void Poser(string relatif)
    {
        var chemin = Path.Combine(_systeme, relatif.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllText(chemin, "x");
    }

    [Theory]
    [InlineData("box3d", ".png", MediaKinds.Box3d)]
    [InlineData("fanart", ".jpg", MediaKinds.Fanart)]
    [InlineData("screenshot", ".png", MediaKinds.Thumbnail)]
    [InlineData("titlescreen", ".png", MediaKinds.Image)]
    [InlineData("wheel", ".png", MediaKinds.Wheel)]
    [InlineData("videos", ".mp4", MediaKinds.Video)]
    [InlineData("manuals", ".pdf", MediaKinds.Manual)]
    public void Le_dossier_de_type_dit_le_type(string dossier, string extension, string attendu)
    {
        Assert.True(MediaQualificationService.TryQualifyByTypeFolder(dossier, extension, out var kind));
        Assert.Equal(attendu, kind);
    }

    [Theory]
    [InlineData("images", ".png")]            // dossier mixte : seul le nom du fichier peut dire
    [InlineData("downloaded_images", ".png")]
    [InlineData("videos", ".png")]            // extension qui ne convient pas au type
    [InlineData("fanart", ".mp4")]
    public void Un_dossier_mixte_ou_une_extension_etrangere_ne_dit_rien(string dossier, string extension)
        => Assert.False(MediaQualificationService.TryQualifyByTypeFolder(dossier, extension, out _));

    [Fact]
    public void Une_boite_3D_designee_par_thumbnail_reste_une_boite_3D()
    {
        Poser("media/box3d/Super Game-thumb.png");
        var jeu = new XElement("game", new XElement("path", "./Super Game.sfc"), new XElement("thumbnail", "./media/box3d/Super Game-thumb.png"));
        var media = GamelistMediaCatalogReader.ExtractGameMedia(jeu, _systeme, _qual);

        Assert.NotNull(media);
        var seul = Assert.Single(media!.Candidates);
        Assert.Equal(MediaKinds.Box3d, seul.Kind);
        Assert.Equal(MediaQualifications.FolderConvention, seul.Qualification);
        Assert.DoesNotContain(media.Candidates, c => c.Kind is MediaKinds.Thumbnail or MediaKinds.BoxFront);
    }

    [Fact]
    public void Les_fichiers_ranges_par_type_sont_trouves_sans_balise()
    {
        Poser("media/fanart/Super Game.png");
        Poser("media/box3d/Super Game.png");
        Poser("media/images/Super Game.png");      // dossier mixte : ignore
        Poser("media/fanart/Autre Jeu.png");       // un autre jeu : ignore
        var lecteur = new GamelistMediaCatalogReader(null!, _qual);

        var trouves = lecteur.TypeFolderCandidates(_systeme, "./Super Game.sfc", already: null);

        Assert.Equal(new[] { MediaKinds.Box3d, MediaKinds.Fanart }, trouves.Select(c => c.Kind).OrderBy(k => k, StringComparer.Ordinal));
        Assert.All(trouves, c => Assert.Equal(MediaQualifications.FolderConvention, c.Qualification));
        Assert.Empty(lecteur.TypeFolderCandidates(_systeme, "./Inconnu.sfc", already: null));
    }

    [Fact]
    public void Un_fichier_deja_cite_par_la_gamelist_n_est_pas_repete()
    {
        Poser("media/fanart/Super Game.png");
        var jeu = new XElement("game", new XElement("path", "./Super Game.sfc"), new XElement("fanart", "./media/fanart/Super Game.png"));
        var cite = GamelistMediaCatalogReader.ExtractGameMedia(jeu, _systeme, _qual);
        var lecteur = new GamelistMediaCatalogReader(null!, _qual);

        Assert.Empty(lecteur.TypeFolderCandidates(_systeme, "./Super Game.sfc", cite));
    }

    [Fact]
    public void L_index_du_magasin_reconnait_un_dossier_de_type()
    {
        var index = new LocalMediaIndexService(new SystemIdNormalizer(new MediaReferenceCatalog()));

        Assert.True(index.TryParseMediaCandidateFromRelativePath("snes/fanart/super-game.png", "media/user", 0, out var fanart));
        Assert.Equal(MediaKinds.Fanart, fanart.Kind);

        // ce que l'index lisait deja ne change pas
        Assert.True(index.TryParseMediaCandidateFromRelativePath("snes/games/super-game/artwork/fanart.png", "media", 1, out var canonique));
        Assert.Equal(MediaKinds.Fanart, canonique.Kind);
        Assert.Equal(canonique.GameSlug, fanart.GameSlug);   // le meme jeu que le rangement canonique
        Assert.False(index.TryParseMediaCandidateFromRelativePath("snes/audio/theme.png", "media", 1, out _));
    }
}
