using RetroBat.Api.Netplay;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Repli direct de l'invite netplay : une valeur de la plateforme ou de l'hote reste UN argument du lanceur.
/// </summary>
public class NetplayArgumentsInviteTests
{
    [Theory]
    [InlineData("motdepasse", "\"motdepasse\"")]
    [InlineData("mot de passe !@#", "\"mot de passe !@#\"")]
    [InlineData("x\" -rom \"c:\\autre.zip", "\"x -rom c:\\autre.zip\"")]
    [InlineData("fin\\", "\"fin\\\\\"")]
    [InlineData("a\r\n-core b", "\"a-core b\"")]
    [InlineData(@"E:\RetroBat\roms\nes\Super Mario Bros. (World).nes", "\"E:\\RetroBat\\roms\\nes\\Super Mario Bros. (World).nes\"")]
    [InlineData(null, "\"\"")]
    public void Une_valeur_reste_un_seul_argument(string? valeur, string attendu)
    {
        Assert.Equal(attendu, NetplayGuestService.EntreGuillemets(valeur));
    }

    [Theory]
    [InlineData("relay.nelfeplay.com", "relay.nelfeplay.com")]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("[2001:db8::1]", "[2001:db8::1]")]
    [InlineData("1.2.3.4 -rom x", "1.2.3.4-romx")]
    [InlineData("a\"b", "ab")]
    public void L_adresse_du_relais_ne_garde_que_des_caracteres_d_adresse(string adresse, string attendu)
    {
        Assert.Equal(attendu, NetplayGuestService.AdresseSure(adresse));
    }
}
