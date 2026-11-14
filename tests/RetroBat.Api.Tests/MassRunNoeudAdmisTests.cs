using System;
using RetroBat.Api.MassRun;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La route publique de jonction MASS RUN n'envoie le billet signe de la borne qu'a un noeud connu.
/// </summary>
public class MassRunNoeudAdmisTests
{
    private static readonly string[] Carte = ["https://node1.nelfeplay.com", "https://massrun.example.org:8443/"];

    [Theory]
    [InlineData("http://127.0.0.1:8082")]
    [InlineData("https://127.0.0.1:8082")]
    [InlineData("http://localhost:8082")]
    [InlineData("http://[::1]:8082")]
    public void Un_noeud_de_la_machine_est_admis(string noeud)
    {
        Assert.True(MassRunService.NoeudAdmis(new Uri(noeud), null, Carte));
    }

    [Theory]
    [InlineData("https://node1.nelfeplay.com")]
    [InlineData("https://NODE1.nelfeplay.com/")]
    [InlineData("https://massrun.example.org:8443")]
    public void Un_noeud_massrun_de_la_carte_en_https_est_admis(string noeud)
    {
        Assert.True(MassRunService.NoeudAdmis(new Uri(noeud), null, Carte));
    }

    [Theory]
    [InlineData("http://node1.nelfeplay.com")]
    [InlineData("https://node1.nelfeplay.com:8443")]
    [InlineData("https://evil.example")]
    [InlineData("http://192.168.1.50:8082")]
    [InlineData("ftp://127.0.0.1")]
    public void Tout_autre_noeud_est_refuse(string noeud)
    {
        Assert.False(MassRunService.NoeudAdmis(new Uri(noeud), null, Carte));
    }

    [Fact]
    public void Le_noeud_impose_par_la_machine_est_admis_meme_en_http()
    {
        Assert.True(MassRunService.NoeudAdmis(new Uri("http://192.168.1.50:8082"), "http://192.168.1.50:8082", Carte));
        Assert.False(MassRunService.NoeudAdmis(new Uri("http://192.168.1.51:8082"), "http://192.168.1.50:8082", Carte));
    }
}
