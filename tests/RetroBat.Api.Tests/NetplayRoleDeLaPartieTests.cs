using RetroBat.Api.Netplay;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La partie d'un autre (2026-10-16) : une borne qui regarde ou rejoint un netplay ne l'enregistre pas, ne la filme pas
/// et ne la propose a aucun direct. Le role reste celui de la partie en cours jusqu'a la suivante.
/// </summary>
public sealed class NetplayRoleDeLaPartieTests
{
    [Fact]
    public void Spectatrice_ou_invitee_la_partie_n_est_pas_a_la_borne()
    {
        try
        {
            NetplayGuestService.NoterRoleDeLaPartie(NetplayGuestService.Role.Spectateur);
            Assert.False(NetplayGuestService.PartieALaBorne);
            NetplayGuestService.NoterRoleDeLaPartie(NetplayGuestService.Role.Joueur);
            Assert.False(NetplayGuestService.PartieALaBorne);
            NetplayGuestService.NoterRoleDeLaPartie(NetplayGuestService.Role.Aucun);
            Assert.True(NetplayGuestService.PartieALaBorne);
        }
        finally
        {
            NetplayGuestService.NoterRoleDeLaPartie(NetplayGuestService.Role.Aucun);
        }
    }
}
