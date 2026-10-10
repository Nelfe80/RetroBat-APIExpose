using RetroBat.Api.Replay.Models;
using RetroBat.Api.Replay.Playback;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le joueur de la carte d'un replay (2026-10-10) : le replay d'un autre joueur, recu a la lecture, s'affichait au nom du
/// proprietaire de la borne. Le pseudo de la borne ne vaut que pour un replay enregistre ici ; sinon la plateforme donne
/// le joueur du record.
/// </summary>
public class ReplayCardPlayerTests
{
    private static ReplayLocalMetadata Meta(bool ici, string? joueur = null) => new(
        ReplayLocalMetadata.SchemaId, "rp_x", "public", false, null, null, "mirrored", DateTime.UtcNow, ici, null, joueur);

    [Fact]
    public void Un_replay_recu_d_ailleurs_ne_prend_pas_le_pseudo_de_la_borne()
    {
        Assert.Equal("JOUEUR", ReplayPlaybackService.JoueurDeLaCarte(null, "player-de-la-borne", "JOUEUR"));
        Assert.Equal("JOUEUR", ReplayPlaybackService.JoueurDeLaCarte(Meta(ici: false), "player-de-la-borne", "JOUEUR"));
    }

    [Fact]
    public void Un_replay_enregistre_ici_prend_le_pseudo_de_la_borne_ou_celui_estampille()
    {
        Assert.Equal("player-de-la-borne", ReplayPlaybackService.JoueurDeLaCarte(Meta(ici: true), "player-de-la-borne", "JOUEUR"));
        Assert.Equal("JOUEUR", ReplayPlaybackService.JoueurDeLaCarte(Meta(ici: true), null, "JOUEUR"));
        Assert.Equal("player-du-record", ReplayPlaybackService.JoueurDeLaCarte(Meta(ici: false, "player-du-record"), "player-de-la-borne", "JOUEUR"));
    }

    [Fact]
    public void La_plateforme_donne_le_joueur_du_record_et_la_certification()
    {
        var carte = new ReplayPlaybackService.ReplayCard("Ms Pac Man", "Fb Alpha", "10 oct. 2026", "JOUEUR", null, null, false);

        var completee = ReplayPlaybackService.CompleterLaCarte(carte, 30260, 1, " player-du-record ");
        Assert.Equal("player-du-record", completee.Player);
        Assert.Equal(30260, completee.Score);
        Assert.Equal(1, completee.Rank);
        Assert.True(completee.Certified);

        // Sans joueur dans la reponse, la carte garde le sien.
        Assert.Equal("JOUEUR", ReplayPlaybackService.CompleterLaCarte(carte, 30260, 1, null).Player);
    }
}
