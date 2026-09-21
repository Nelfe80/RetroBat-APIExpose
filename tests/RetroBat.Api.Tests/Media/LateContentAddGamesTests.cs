using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests.Media;

/// <summary>
/// Le jeton de RATTRAPAGE d'une fiche consultee.
///
/// Regle du projet : un meme game-selected ne produit qu'un seul /addgames. Une exception
/// existait pour la video fraichement scrapee. Mesure du 2026-09-21 sur zaviga et zaxxon : les
/// medias locaux partaient en une seconde et depensaient l'unique envoi, puis la description
/// anglaise arrivait 11 s plus tard et se voyait refuser l'entree, alors qu'elle etait ecrite
/// sur le disque. La fiche restait muette toute la session.
///
/// Le jeton vaut donc desormais pour la video comme pour la premiere description, et il n'en
/// existe toujours qu'UN : les deux voyagent ensemble quand elles arrivent dans la meme passe
/// (demande du user).
/// </summary>
public class LateContentAddGamesTests
{
    private const string Systeme = "fbneo";
    private const string Chemin = @"E:\RetroBat\roms\fbneo\zaviga.zip";

    private static MediaRuntimeState EtatSurLaFiche()
    {
        var etat = new MediaRuntimeState();
        etat.RecordGameSelectedSelection(Systeme, Chemin);
        return etat;
    }

    [Fact]
    public void Le_premier_envoi_d_une_fiche_passe()
    {
        var etat = EtatSurLaFiche();

        Assert.False(etat.ShouldSuppressLiveAddGamesForSelection(Systeme, Chemin, out _));
    }

    [Fact]
    public void Un_second_envoi_sans_contenu_tardif_est_refuse()
    {
        var etat = EtatSurLaFiche();
        etat.MarkLiveAddGamesPushedForSelection(Systeme, Chemin);

        Assert.True(etat.ShouldSuppressLiveAddGamesForSelection(Systeme, Chemin, out var raison));
        Assert.Equal("already-pushed-current-selection", raison);
    }

    /// <summary>Le cas zaviga : la description arrive apres l'envoi des images.</summary>
    [Fact]
    public void Une_premiere_description_arrivee_apres_obtient_son_rattrapage()
    {
        var etat = EtatSurLaFiche();
        etat.MarkLiveAddGamesPushedForSelection(Systeme, Chemin);

        Assert.False(etat.ShouldSuppressLiveAddGamesForSelection(
            Systeme,
            Chemin,
            out _,
            allowLateContentException: true));
    }

    [Fact]
    public void Le_rattrapage_ne_sert_qu_une_fois()
    {
        var etat = EtatSurLaFiche();
        etat.MarkLiveAddGamesPushedForSelection(Systeme, Chemin);
        // le rattrapage part (texte, video, ou les deux)
        etat.MarkLiveAddGamesPushedForSelection(Systeme, Chemin);

        Assert.True(etat.ShouldSuppressLiveAddGamesForSelection(
            Systeme,
            Chemin,
            out var raison,
            allowLateContentException: true));
        Assert.Equal("already-pushed-current-selection-late-content", raison);
    }

    /// <summary>
    /// Le premier envoi ne doit JAMAIS consommer le rattrapage, meme s'il porte deja du texte :
    /// sinon la video du meme voyage serait condamnee.
    /// </summary>
    [Fact]
    public void Le_premier_envoi_ne_consomme_pas_le_rattrapage()
    {
        var etat = EtatSurLaFiche();
        etat.MarkLiveAddGamesPushedForSelection(Systeme, Chemin);

        Assert.False(etat.ShouldSuppressLiveAddGamesForSelection(
            Systeme,
            Chemin,
            out _,
            allowLateContentException: true));
    }

    [Fact]
    public void Une_autre_fiche_garde_son_propre_jeton()
    {
        var etat = EtatSurLaFiche();
        etat.MarkLiveAddGamesPushedForSelection(Systeme, Chemin);
        etat.MarkLiveAddGamesPushedForSelection(Systeme, Chemin);

        const string autre = @"E:\RetroBat\roms\fbneo\zaxxon.zip";
        etat.RecordGameSelectedSelection(Systeme, autre);

        Assert.False(etat.ShouldSuppressLiveAddGamesForSelection(Systeme, autre, out _));
    }

    [Fact]
    public void Une_fiche_qui_n_est_plus_celle_affichee_ne_recoit_rien()
    {
        var etat = EtatSurLaFiche();
        etat.RecordGameSelectedSelection(Systeme, @"E:\RetroBat\roms\fbneo\zaxxon.zip");

        Assert.True(etat.ShouldSuppressLiveAddGamesForSelection(
            Systeme,
            Chemin,
            out var raison,
            allowLateContentException: true));
        Assert.Equal("not-current-selection", raison);
    }
}
