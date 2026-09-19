using RetroBat.Api.Media;
using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// Ce qui autorise APIExpose a depenser l'unique /addgames d'une fiche.
///
/// Regle du projet (docs/03_FEAT_SCRAPING.md) : un meme game-selected ne produit qu'un seul
/// /addgames, sauf l'exception video, et seulement pour un vrai changement de ce qu'ES dessine.
/// Le raccourci « la carte etait nue a l'arrivee » contournait la comparaison : mesure du
/// 2026-09-19 sur Sonic, son seul logo introuvable rendait la carte « nue » a chaque visite, et
/// un fragment identique a la gamelist partait quand meme.
/// </summary>
public class LiveRenderDeltaTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "delta-" + Guid.NewGuid().ToString("N")[..8]);

    public LiveRenderDeltaTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Fichier(string nom)
    {
        var chemin = Path.Combine(_dossier, nom);
        File.WriteAllText(chemin, nom);
        return chemin;
    }

    /// <summary>Le plan tel que la borne le construit : image et vignette visibles, logo aussi.</summary>
    private static MediaProjectionPlan Plan(params MediaNeed[] besoins) => new()
    {
        SystemId = "megadrive",
        FrontendSystemId = "megadrive",
        GameSlug = "sonic_the_hedgehog",
        PreferredImageSource = "sstitle",
        PreferredThumbnailSource = "box2d",
        PreferredLogoSource = "logo",
        Needs = besoins.ToList(),
    };

    private static MediaNeed Besoin(string kind, string initial = "", string existant = "", string projete = "") => new()
    {
        Kind = kind,
        IsMissing = existant.Length == 0 && projete.Length == 0,
        InitialExistingPath = initial,
        ExistingPath = existant,
        ProjectedPath = projete,
    };

    [Fact]
    public void Un_emplacement_vide_a_l_arrivee_et_rempli_depuis_compte_comme_neuf()
    {
        var image = Fichier("screentitle.png");
        var plan = Plan(Besoin("sstitle", initial: "", projete: image));

        Assert.True(MediaPrefetchService.HasVisibleSlotFilledDuringSelection(plan, "steel"));
    }

    [Fact]
    public void Un_emplacement_deja_rempli_a_l_arrivee_ne_compte_pas()
    {
        var image = Fichier("screentitle.png");
        var plan = Plan(Besoin("sstitle", initial: image, existant: image));

        Assert.False(MediaPrefetchService.HasVisibleSlotFilledDuringSelection(plan, "steel"));
    }

    /// <summary>
    /// Le cas Sonic : le logo reste introuvable, tout le reste etait deja la. Rien n'a ete
    /// comble pendant cette selection, donc il n'y a rien a peindre et le fragment doit etre
    /// compare a la gamelist comme n'importe quel autre.
    /// </summary>
    [Fact]
    public void Un_type_introuvable_ne_rend_pas_la_carte_neuve()
    {
        var image = Fichier("screentitle.png");
        var plan = Plan(
            Besoin("sstitle", initial: image, existant: image),
            Besoin("logo"));

        Assert.Contains(plan.Needs, besoin => besoin.IsMissing);
        Assert.False(MediaPrefetchService.HasVisibleSlotFilledDuringSelection(plan, "steel"));
    }

    [Fact]
    public void Un_media_non_affiche_par_la_fiche_ne_compte_pas()
    {
        var cartouche = Fichier("cartridge.png");
        var plan = Plan(Besoin("cartridge", initial: "", projete: cartouche));

        Assert.False(MediaPrefetchService.HasVisibleSlotFilledDuringSelection(plan, "steel"));
    }

    [Fact]
    public void Un_emplacement_annonce_rempli_mais_sans_fichier_ne_compte_pas()
    {
        var plan = Plan(Besoin("sstitle", initial: "", projete: Path.Combine(_dossier, "absent.png")));

        Assert.False(MediaPrefetchService.HasVisibleSlotFilledDuringSelection(plan, "steel"));
    }

    [Fact]
    public void Le_fanart_compte_toujours_comme_visible()
    {
        var fanart = Fichier("fanart.jpg");
        var plan = Plan(Besoin("fanart", initial: "", projete: fanart));

        Assert.True(MediaPrefetchService.HasVisibleSlotFilledDuringSelection(plan, "steel"));
    }
}
