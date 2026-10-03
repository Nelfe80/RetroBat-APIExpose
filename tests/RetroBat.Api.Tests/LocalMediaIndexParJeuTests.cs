using RetroBat.Api.Media;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// L'index d'un seul jeu (2026-10-03) doit donner, pour ce jeu, exactement ce que donne l'index
/// complet du systeme, en lisant beaucoup moins de fichiers : la decision de recherche distante
/// et l'envoi en direct a ES parcouraient tout le systeme (7,5 s de disque en arcade).
/// </summary>
public sealed class LocalMediaIndexParJeuTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "nelfe-medias-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { }
    }

    private void Fichier(string relatif)
    {
        var chemin = Path.Combine(_racine, relatif.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllBytes(chemin, new byte[] { 1 });
    }

    private IReadOnlyList<(string Root, string SourceRoot, int Priority)> Racines() =>
    [
        (Path.Combine(_racine, "user"), "media/user", 0),
        (Path.Combine(_racine, "systems"), "media", 1),
    ];

    private static HashSet<string> PourLesNoms(LocalMediaIndex index, IEnumerable<string> kinds, IReadOnlyCollection<string> noms)
        => kinds.SelectMany(kind => index.GetCandidates("megadrive", kind))
            .Where(c => noms.Contains(c.GameSlug, StringComparer.OrdinalIgnoreCase) || noms.Contains(c.FamilySlug, StringComparer.OrdinalIgnoreCase))
            .Select(c => c.Path.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void L_index_d_un_jeu_rend_ce_que_rend_l_index_complet_pour_ce_jeu()
    {
        // Le jeu, un clone de la meme famille, un autre jeu, et des fichiers hors de games/.
        Fichier("systems/megadrive/games/sonic/artwork/box/front.png");
        Fichier("systems/megadrive/games/sonic/sonic-eu-boxfront.png");
        Fichier("systems/megadrive/games/sonic/ui/wheels/wheel.png");
        Fichier("systems/megadrive/games/sonic (rev 1)/artwork/box/front.png");
        Fichier("systems/megadrive/games/streets of rage/artwork/box/front.png");
        Fichier("systems/megadrive/games/streets of rage/sonic-fr-wheel.png");   // chez un autre jeu : a lui
        Fichier("systems/megadrive/artwork/sonic-fr-wheel.png");                 // hors de games/ : au jeu
        Fichier("systems/megadrive/ui/wheels/wheel.megadrive.png");
        Fichier("user/megadrive/games/sonic/artwork/box/front.png");
        for (var i = 0; i < 40; i++) Fichier($"systems/megadrive/games/jeu{i}/artwork/box/front.png");

        var service = new LocalMediaIndexService(new SystemIdNormalizer(new MediaReferenceCatalog()));
        var complet = service.Build(new[] { "megadrive" }, Racines());
        var noms = new[] { "sonic" };
        var parJeu = service.BuildForGames("megadrive", noms, Racines());

        var kinds = new[] { MediaKinds.BoxFront, MediaKinds.Wheel, MediaKinds.Thumbnail };
        var attendus = PourLesNoms(complet, kinds, noms);
        Assert.NotEmpty(attendus);
        Assert.Equal(attendus, PourLesNoms(parJeu, kinds, noms));
        Assert.True(parJeu.ScannedFiles < complet.ScannedFiles / 4, $"{parJeu.ScannedFiles} fichiers lus contre {complet.ScannedFiles}");
    }

    [Fact]
    public void Un_systeme_sans_medias_rend_un_index_vide()
    {
        var service = new LocalMediaIndexService(new SystemIdNormalizer(new MediaReferenceCatalog()));
        var index = service.BuildForGames("megadrive", new[] { "sonic" }, Racines());
        Assert.Empty(index.GetCandidates("megadrive", MediaKinds.BoxFront));
    }
}
