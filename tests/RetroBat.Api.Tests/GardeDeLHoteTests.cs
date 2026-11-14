using System;
using System.Collections.Generic;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Garde de l'hote (rebond DNS) et ecritures de navigateur sans Sec-Fetch-Site.
/// </summary>
public class GardeDeLHoteTests
{
    private static readonly HashSet<string> Noms = GardeDeLHote.NomsAcceptes("borne-salle, Hub.Local.", "http://127.0.0.1:12345");

    [Theory]
    [InlineData("127.0.0.1:12345")]
    [InlineData("127.0.0.1")]
    [InlineData("localhost:12345")]
    [InlineData("LOCALHOST")]
    [InlineData("localhost.:12345")]
    [InlineData("overlay.localhost:12345")]
    [InlineData("[::1]:12345")]
    [InlineData("::1")]
    [InlineData("192.168.1.34:12345")]
    [InlineData("10.0.0.5")]
    [InlineData("borne-salle:12345")]
    [InlineData("hub.local")]
    [InlineData("")]
    [InlineData(null)]
    public void Hotes_legitimes_acceptes(string? hote)
    {
        Assert.True(GardeDeLHote.EstAccepte(hote, Noms));
    }

    [Theory]
    [InlineData("evil.example:12345")]
    [InlineData("rebind.attacker.net")]
    [InlineData("127.0.0.1.nip.io:12345")]
    [InlineData("localhost.evil.com")]
    [InlineData("[]:12345")]
    public void Hotes_etrangers_refuses(string hote)
    {
        Assert.False(GardeDeLHote.EstAccepte(hote, Noms));
    }

    [Fact]
    public void Le_nom_de_la_machine_est_accepte()
    {
        var noms = GardeDeLHote.NomsAcceptes(null, null);
        Assert.True(GardeDeLHote.EstAccepte(Environment.MachineName + ":12345", noms));
        Assert.True(GardeDeLHote.EstAccepte(System.Net.Dns.GetHostName(), noms));
        Assert.True(GardeDeLHote.EstAccepte(Environment.MachineName.ToLowerInvariant() + ".local", noms));
    }

    [Fact]
    public void L_adresse_d_ecoute_nommee_est_acceptee_et_les_jokers_ignores()
    {
        var noms = GardeDeLHote.NomsAcceptes(null, "http://borne7:12345;http://+:12346;http://*:12347;http://0.0.0.0:12345");
        Assert.Contains("borne7", noms);
        Assert.DoesNotContain("joker", noms);
        Assert.DoesNotContain("+", noms);
    }

    [Theory]
    [InlineData(null, GardeDeLHote.Mode.Bloquer)]
    [InlineData("", GardeDeLHote.Mode.Bloquer)]
    [InlineData("enforce", GardeDeLHote.Mode.Bloquer)]
    [InlineData("log", GardeDeLHote.Mode.Journaliser)]
    [InlineData("LOG", GardeDeLHote.Mode.Journaliser)]
    [InlineData("off", GardeDeLHote.Mode.Arret)]
    public void Mode_lu_de_la_configuration(string? valeur, GardeDeLHote.Mode attendu)
    {
        Assert.Equal(attendu, GardeDeLHote.LireMode(valeur));
    }

    [Theory]
    // Clients natifs : aucune Origin.
    [InlineData(null, "127.0.0.1:12345", false)]
    [InlineData("", "127.0.0.1:12345", false)]
    // Pages locales : boucle locale ou meme hote.
    [InlineData("http://127.0.0.1:19780", "127.0.0.1:12345", false)]
    [InlineData("http://localhost:8080", "127.0.0.1:12345", false)]
    [InlineData("http://192.168.1.34:12345", "192.168.1.34:12345", false)]
    // Pages etrangeres.
    [InlineData("http://evil.example", "127.0.0.1:12345", true)]
    [InlineData("https://www.nelfeplay.com", "127.0.0.1:12345", true)]
    [InlineData("null", "127.0.0.1:12345", true)]
    public void Ecriture_sans_SecFetchSite_reconnue_par_Origin(string? origine, string hote, bool etrangere)
    {
        Assert.Equal(etrangere, GardeDeLHote.EcritureDePageEtrangere(origine, hote));
    }

    [Fact]
    public void Marques_de_navigateur()
    {
        Assert.False(GardeDeLHote.MarqueDeNavigateur(_ => null));
        Assert.True(GardeDeLHote.MarqueDeNavigateur(n => n == "Sec-Fetch-Site" ? "cross-site" : null));
        Assert.True(GardeDeLHote.MarqueDeNavigateur(n => n == "Origin" ? "null" : null));
        Assert.True(GardeDeLHote.MarqueDeNavigateur(n => n == "Sec-Fetch-Mode" ? "no-cors" : null));
    }
}
