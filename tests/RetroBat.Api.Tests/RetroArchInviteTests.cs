using System.Net;
using System.Net.NetworkInformation;
using RetroBat.Api.Netplay;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un client RetroArch demande le mot de passe a l'ecran (2026-10-02, RetroArch 1.22.2) : la borne
/// le tape des que RetroArch a joint le relais.
/// </summary>
public sealed class RetroArchInviteTests
{
    private static readonly IPAddress Relais = IPAddress.Parse("34.76.10.20");

    [Fact]
    public void La_connexion_au_relais_se_reconnait_a_son_adresse_et_son_port()
    {
        var connexions = new[]
        {
            new Connexion(IPAddress.Parse("10.0.0.5"), 55435, TcpState.Established),
            new Connexion(Relais, 80, TcpState.Established),
        };
        Assert.False(RetroArchInvite.ConnexionVers(connexions, new[] { Relais }, 55435));

        var etablie = connexions.Append(new Connexion(Relais, 55435, TcpState.Established)).ToArray();
        Assert.True(RetroArchInvite.ConnexionVers(etablie, new[] { Relais }, 55435));

        var enCours = connexions.Append(new Connexion(Relais, 55435, TcpState.SynSent)).ToArray();
        Assert.False(RetroArchInvite.ConnexionVers(enCours, new[] { Relais }, 55435));
    }

    [Fact]
    public void Sans_adresse_resolue_le_port_suffit()
    {
        var connexions = new[] { new Connexion(Relais, 55435, TcpState.Established) };
        Assert.True(RetroArchInvite.ConnexionVers(connexions, Array.Empty<IPAddress>(), 55435));
    }

    [Fact]
    public void Le_mot_de_passe_finit_par_entree()
    {
        Assert.Equal("ab12cd34\r".ToCharArray(), RetroArchInvite.Frappes("ab12cd34"));
    }

    [Fact]
    public void Plusieurs_essais_echelonnes_apres_la_connexion()
    {
        Assert.True(RetroArchInvite.Essais.Length >= 3);
        Assert.True(RetroArchInvite.Essais.Zip(RetroArchInvite.Essais.Skip(1)).All(p => p.First < p.Second));
    }

    private sealed class Connexion : TcpConnectionInformation
    {
        public Connexion(IPAddress distante, int port, TcpState etat)
        {
            RemoteEndPoint = new IPEndPoint(distante, port);
            State = etat;
        }

        public override IPEndPoint LocalEndPoint { get; } = new(IPAddress.Loopback, 50000);
        public override IPEndPoint RemoteEndPoint { get; }
        public override TcpState State { get; }
    }
}
