using RetroBat.Domain.Models;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un /addgames ne redessine que la vue du systeme pousse. Savoir si la vue affichee est
/// detachee de ce systeme est donc ce qui decide si le joueur verra quelque chose, ou s'il
/// faut un rechargement complet. Mesure du 2026-09-18 sur la source d'ES.
/// </summary>
public class DetachedViewReloadTests
{
    [Fact]
    public void Le_carrousel_sur_le_meme_systeme_n_est_pas_detache()
    {
        var etat = new MediaRuntimeState();
        etat.MarkCarouselSystem("fbneo");

        Assert.False(etat.IsViewDetachedFromSystem("fbneo"));
        Assert.False(etat.IsViewDetachedFromSystem("FBNEO"));
    }

    [Fact]
    public void Une_collection_est_detachee_du_systeme_du_jeu()
    {
        var etat = new MediaRuntimeState();
        etat.MarkCarouselSystem("nelfeplay-scoring");

        Assert.True(etat.IsViewDetachedFromSystem("fbneo"));
        Assert.True(etat.IsViewDetachedFromSystem("megadrive"));
    }

    [Fact]
    public void Sans_systeme_connu_on_ne_conclut_rien()
    {
        var etat = new MediaRuntimeState();

        Assert.False(etat.IsViewDetachedFromSystem("fbneo"));
        etat.MarkCarouselSystem("   ");
        Assert.False(etat.IsViewDetachedFromSystem("fbneo"));
        etat.MarkCarouselSystem("fbneo");
        Assert.False(etat.IsViewDetachedFromSystem(string.Empty));
    }

    [Fact]
    public void Quitter_la_collection_rend_la_vue_attachee()
    {
        var etat = new MediaRuntimeState();
        etat.MarkCarouselSystem("nelfeplay-scoring");
        Assert.True(etat.IsViewDetachedFromSystem("fbneo"));

        etat.MarkCarouselSystem("fbneo");
        Assert.False(etat.IsViewDetachedFromSystem("fbneo"));
    }

    /// <summary>
    /// Le rechargement passe par le canal commun : debounce, et rien pendant la fenetre de
    /// suppression. Sans cela, naviguer dans une collection rechargerait la liste a chaque jeu.
    /// </summary>
    [Fact]
    public void Le_rechargement_ne_part_qu_une_fois_par_fenetre()
    {
        var etat = new MediaRuntimeState();

        Assert.True(etat.TryRequestReloadGamesBypassingLastGameSelected(
            TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(12)));
        Assert.False(etat.TryRequestReloadGamesBypassingLastGameSelected(
            TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(12)));
    }

    /// <summary>
    /// La retenue « dernier evenement game-selected » est justement le cas ou on en a besoin :
    /// le contournement doit etre arme par la demande.
    /// </summary>
    [Fact]
    public void Le_rechargement_passe_malgre_une_selection_en_cours()
    {
        var etat = new MediaRuntimeState();
        etat.SetLastFrontendEvent("game-selected");

        etat.TryRequestReloadGamesBypassingLastGameSelected(TimeSpan.Zero, TimeSpan.FromSeconds(12));
        var statut = etat.GetReloadGamesStatus(TimeSpan.Zero);

        Assert.True(statut.Pending);
        Assert.True(statut.Ready);
    }

    /// <summary>
    /// Un rechargement demande par un geste technique n'annonce rien : le joueur n'a pas
    /// commande d'operation, et un toast de progression lui ferait croire le contraire.
    /// </summary>
    [Fact]
    public void Le_rechargement_dune_vue_detachee_est_silencieux()
    {
        var etat = new MediaRuntimeState();

        etat.TryRequestReloadGamesBypassingLastGameSelected(TimeSpan.Zero, TimeSpan.FromSeconds(12), silencieux: true);

        Assert.True(etat.ReloadGamesSilencieux);
    }

    [Fact]
    public void Un_rechargement_ordinaire_garde_sa_barre_de_progression()
    {
        var etat = new MediaRuntimeState();

        etat.TryRequestReloadGamesBypassingLastGameSelected(TimeSpan.Zero, TimeSpan.FromSeconds(12));

        Assert.False(etat.ReloadGamesSilencieux);
    }

    [Fact]
    public void Le_silence_ne_survit_pas_au_rechargement()
    {
        var etat = new MediaRuntimeState();
        etat.TryRequestReloadGamesBypassingLastGameSelected(TimeSpan.Zero, TimeSpan.FromSeconds(12), silencieux: true);

        etat.ConsumeReloadGamesPending();

        Assert.False(etat.ReloadGamesSilencieux);
    }
}
