using RetroBat.Api.Scoring;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le port de la borne, retrouve en rapprochant les appuis de son panel de ceux de chaque port
/// (1CC MULTI). Les appuis locaux suivent le panel ; ceux de l'autre joueur n'ont rien a voir.
/// </summary>
public class PortLocalTests
{
    // Deux joueurs qui appuient a leur rythme, sans lien entre eux.
    private static readonly int[] Hote = [3, 0, 5, 1, 4, 0, 2, 6, 0, 3, 1, 5, 0, 4, 2, 0, 6, 1, 3, 0];
    private static readonly int[] Invite = [0, 4, 1, 5, 0, 3, 6, 0, 4, 1, 5, 0, 3, 1, 6, 2, 0, 4, 1, 5];

    private static int[][] Ports() => Enumerable.Range(0, Hote.Length).Select(i => new[] { Hote[i], Invite[i], 0, 0 }).ToArray();

    [Fact]
    public void L_invite_retrouve_le_port_du_joueur_2()
    {
        Assert.Equal(1, PortLocal.Estimer(Ports(), Invite));
    }

    [Fact]
    public void L_hote_retrouve_le_port_du_joueur_1()
    {
        Assert.Equal(0, PortLocal.Estimer(Ports(), Hote));
    }

    [Fact]
    public void Un_decalage_d_une_seconde_ne_trompe_pas()
    {
        // La moitie des appuis de chaque seconde tombe, cote panel, dans la seconde suivante.
        var panel = new int[Invite.Length];
        for (var i = 0; i < Invite.Length; i++)
        {
            var moitie = Invite[i] / 2;
            panel[i] += Invite[i] - moitie;
            if (i + 1 < panel.Length) panel[i + 1] += moitie;
        }
        Assert.Equal(1, PortLocal.Estimer(Ports(), panel));
    }

    [Fact]
    public void Trop_peu_d_appuis_ne_tranche_pas()
    {
        var peu = new int[Hote.Length];
        peu[3] = 2;
        peu[9] = 1;
        Assert.Null(PortLocal.Estimer(Ports(), peu));
    }

    [Fact]
    public void Deux_ports_qui_se_ressemblent_ne_tranchent_pas()
    {
        var ports = Enumerable.Range(0, Hote.Length).Select(i => new[] { Hote[i], Hote[i], 0, 0 }).ToArray();
        Assert.Null(PortLocal.Estimer(ports, Hote));
    }

    [Fact]
    public void Le_suivi_en_direct_donne_le_meme_port()
    {
        var suivi = new PortLocal();
        for (var i = 0; i < Hote.Length; i++)
        {
            for (var k = 0; k < Invite[i]; k++) suivi.AppuiDuPanel();
            suivi.SecondeDuWrapper([Hote[i], Invite[i], 0, 0]);
        }
        Assert.Equal(1, suivi.Estimer());
        suivi.Vider();
        Assert.Null(suivi.Estimer());
    }
}
