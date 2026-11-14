using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Lot 4 bis C-9 (14/11) : aucun minuteur n'avait de recul apres echec, un seul avait de la gigue. Recul exponentiel et
/// dispersion de 10 % pour agent/work, open-games, challenges/open, avatar/pending, live/following.
/// </summary>
public sealed class CadenceDeFondTests
{
    private static readonly TimeSpan Cinq = TimeSpan.FromMinutes(5);

    [Fact]
    public void Sans_echec_la_base_dispersee_de_dix_pour_cent()
    {
        Assert.Equal(Cinq, CadenceDeFond.Prochaine(Cinq, 0, TimeSpan.FromHours(1), 0.5));
        Assert.Equal(TimeSpan.FromSeconds(270), CadenceDeFond.Prochaine(Cinq, 0, TimeSpan.FromHours(1), 0));
        Assert.Equal(TimeSpan.FromSeconds(330), CadenceDeFond.Prochaine(Cinq, 0, TimeSpan.FromHours(1), 1));
    }

    [Fact]
    public void Chaque_echec_double_l_attente_jusqu_au_plafond()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), CadenceDeFond.Prochaine(Cinq, 1, TimeSpan.FromHours(1), 0.5));
        Assert.Equal(TimeSpan.FromMinutes(40), CadenceDeFond.Prochaine(Cinq, 3, TimeSpan.FromHours(1), 0.5));
        Assert.Equal(TimeSpan.FromHours(1), CadenceDeFond.Prochaine(Cinq, 4, TimeSpan.FromHours(1), 0.5));
        Assert.Equal(TimeSpan.FromHours(1), CadenceDeFond.Prochaine(Cinq, 200, TimeSpan.FromHours(1), 0.5));
    }

    [Fact]
    public void Un_plafond_plus_court_que_la_base_ne_raccourcit_jamais_la_base()
    {
        Assert.Equal(Cinq, CadenceDeFond.Prochaine(Cinq, 2, TimeSpan.FromMinutes(1), 0.5));
    }
}
