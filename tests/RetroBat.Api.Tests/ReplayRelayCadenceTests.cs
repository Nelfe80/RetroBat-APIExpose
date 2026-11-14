using RetroBat.Api.Replay.Sharing;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Lot 4 bis C-3 (14/11) : <c>agent/relay/inbox</c>, 9 422 releves par jour (60 s au repos, 15 s pendant toute partie). Au
/// repos 5 min ; 15 s seulement pour un spectateur d'un direct, une salle MULTIPLAYER ou des planches attendues.
/// </summary>
public sealed class ReplayRelayCadenceTests
{
    [Fact]
    public void Au_repos_comme_en_partie_solo_le_releve_part_toutes_les_cinq_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), ReplayRelayService.Delai(false, false, false, false, 0, 0.5));
    }

    [Fact]
    public void Spectateur_salle_ou_planches_attendues_quinze_secondes()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), ReplayRelayService.Delai(true, false, false, false, 0, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(15), ReplayRelayService.Delai(false, true, false, false, 0, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(15), ReplayRelayService.Delai(false, false, true, true, 0, 0.5));
    }

    [Fact]
    public void Un_replay_demande_au_relais_se_cherche_chaque_minute()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), ReplayRelayService.Delai(false, false, false, true, 0, 0.5));
    }

    [Fact]
    public void Apres_des_releves_rates_l_attente_double_jusqu_a_trente_minutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ReplayRelayService.Delai(true, false, false, false, 1, 0.5));
        Assert.Equal(TimeSpan.FromMinutes(10), ReplayRelayService.Delai(false, false, false, false, 1, 0.5));
        Assert.Equal(TimeSpan.FromMinutes(30), ReplayRelayService.Delai(false, false, false, false, 9, 0.5));
    }

    [Fact]
    public void Les_longues_attentes_sont_dispersees()
    {
        Assert.Equal(TimeSpan.FromSeconds(270), ReplayRelayService.Delai(false, false, false, false, 0, 0));
        Assert.Equal(TimeSpan.FromSeconds(330), ReplayRelayService.Delai(false, false, false, false, 0, 1));
        Assert.Equal(TimeSpan.FromSeconds(15), ReplayRelayService.Delai(true, false, false, false, 0, 0));
    }

    [Fact]
    public void L_etat_multiplayer_du_bus_dit_s_il_y_a_une_salle()
    {
        Assert.True(ReplayRelayService.SalleDans(new { rooms = new[] { new { room = "r1" } }, invitations = Array.Empty<object>() }));
        Assert.False(ReplayRelayService.SalleDans(new { rooms = Array.Empty<object>() }));
        Assert.False(ReplayRelayService.SalleDans(null));
        Assert.False(ReplayRelayService.SalleDans("illisible"));
    }
}
