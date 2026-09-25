using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un cœur qu'on enveloppe sans y rien mesurer est un habitant de plus dans le processus de
/// RetroArch, pour rien. Sous le cœur libretro MAME c'est le plugin Lua qui mesure, et le wrapper
/// le dit lui-même : « aucune RAM lisible ! system_ram=NULL ». RetroArch y plante en 0xC0000005,
/// sept fois le 24 septembre 2026, au démarrage de la partie (crédit puis start).
///
/// D'où cette liste d'exclusion. Ce qui compte et que ces tests gèlent : exclure DÉFAIT ce qui est
/// déjà posé. Une borne déployée garderait sinon son shim pour toujours, et l'exclusion ne serait
/// qu'une intention.
/// </summary>
public class RetroArchWrapperExclusionTests
{
    private static readonly string[] Liste = ["mame_libretro"];

    [Fact]
    public void Le_nom_se_reconnait_avec_ou_sans_le_dll()
    {
        // C'est « mame_libretro » qu'on écrit dans les appsettings ; le service compare au nom de
        // fichier. Les deux formes doivent marcher, sinon la liste ne prend pas et personne ne
        // comprend pourquoi.
        Assert.True(RetroArchWrapperDeploymentService.EstExclu("mame_libretro.dll", Liste));
        Assert.True(RetroArchWrapperDeploymentService.EstExclu("mame_libretro.dll", ["mame_libretro.dll"]));
        Assert.True(RetroArchWrapperDeploymentService.EstExclu("MAME_LIBRETRO.DLL", Liste));
        Assert.True(RetroArchWrapperDeploymentService.EstExclu("mame_libretro.dll", ["  mame_libretro  "]));
    }

    [Fact]
    public void Un_autre_coeur_n_est_pas_touche()
    {
        // La ressemblance des noms est le piège : `mame2003_plus_libretro` et `mame2010_libretro`
        // sont d'autres cœurs, et FBNeo mesure très bien. Aucun ne doit sortir du wrapper parce
        // qu'on a exclu MAME.
        Assert.False(RetroArchWrapperDeploymentService.EstExclu("fbneo_libretro.dll", Liste));
        Assert.False(RetroArchWrapperDeploymentService.EstExclu("mame2003_plus_libretro.dll", Liste));
        Assert.False(RetroArchWrapperDeploymentService.EstExclu("mame2010_libretro.dll", Liste));
        Assert.False(RetroArchWrapperDeploymentService.EstExclu("mame_libretro_extra.dll", Liste));
    }

    [Fact]
    public void Une_liste_vide_ou_sale_n_exclut_rien()
    {
        // Le défaut de la flotte est une liste vide : rien ne doit changer pour personne.
        Assert.False(RetroArchWrapperDeploymentService.EstExclu("mame_libretro.dll", []));
        Assert.False(RetroArchWrapperDeploymentService.EstExclu("mame_libretro.dll", ["", "   "]));
    }

    [Fact]
    public void Un_coeur_exclu_qui_porte_le_shim_est_REMIS_EN_PLACE()
    {
        // LE POINT DE TOUT. Sans cela l'exclusion ne serait qu'une intention : le shim resterait
        // en place sur chaque borne déjà déployée, donc sur toutes.
        var (restore, deploy, refresh) = RetroArchWrapperDeploymentService.Arbitrer(
            exclu: true, isWrapper: true, hasRealCore: true, estLaReference: true);

        Assert.True(restore);
        Assert.False(deploy);
        Assert.False(refresh);
    }

    [Fact]
    public void Un_coeur_exclu_sans_copie_du_vrai_binaire_ne_se_touche_pas()
    {
        // Il n'y a rien à remettre : écraser le shim laisserait le cœur absent, ce qui est pire
        // qu'un cœur enveloppé. Le statut le dit en clair, et on ne touche à rien.
        var (restore, deploy, refresh) = RetroArchWrapperDeploymentService.Arbitrer(
            exclu: true, isWrapper: true, hasRealCore: false, estLaReference: true);

        Assert.False(restore);
        Assert.False(deploy);
        Assert.False(refresh);
    }

    [Fact]
    public void Un_coeur_exclu_deja_remis_reste_tranquille()
    {
        // Deuxième passage : `cores/` porte le vrai cœur. Il ne doit ni repartir dans cores_real
        // ni se faire réenvelopper, sinon l'API le shimerait à chaque démarrage.
        var (restore, deploy, refresh) = RetroArchWrapperDeploymentService.Arbitrer(
            exclu: true, isWrapper: false, hasRealCore: true, estLaReference: false);

        Assert.False(restore);
        Assert.False(deploy);
        Assert.False(refresh);
    }

    [Fact]
    public void Sans_exclusion_rien_ne_change_pour_les_autres()
    {
        // La régression à ne pas commettre : 157 cœurs continuent de se comporter exactement
        // comme avant.
        Assert.Equal((false, true, false),
            RetroArchWrapperDeploymentService.Arbitrer(false, isWrapper: false, hasRealCore: false, estLaReference: false));
        Assert.Equal((false, true, false),
            RetroArchWrapperDeploymentService.Arbitrer(false, isWrapper: false, hasRealCore: true, estLaReference: false));
        Assert.Equal((false, false, true),
            RetroArchWrapperDeploymentService.Arbitrer(false, isWrapper: true, hasRealCore: true, estLaReference: false));
        Assert.Equal((false, false, false),
            RetroArchWrapperDeploymentService.Arbitrer(false, isWrapper: true, hasRealCore: true, estLaReference: true));
        Assert.Equal((false, false, false),
            RetroArchWrapperDeploymentService.Arbitrer(false, isWrapper: true, hasRealCore: false, estLaReference: true));
    }
}
