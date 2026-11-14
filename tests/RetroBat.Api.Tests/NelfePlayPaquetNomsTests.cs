using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Installation d'un paquet : les noms venus du serveur restent des noms simples, jamais un chemin ni un script.
/// </summary>
public class NelfePlayPaquetNomsTests
{
    [Theory]
    [InlineData("Super Game (USA).zip")]
    [InlineData("nes")]
    [InlineData("1.0.3")]
    [InlineData("super-game")]
    public void Noms_ordinaires_acceptes(string nom)
    {
        Assert.True(NelfePlayAgentService.NomSimple(nom));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(@"..\..\x.bat")]
    [InlineData("../x")]
    [InlineData(@"C:\Users\x")]
    [InlineData("c:x")]
    [InlineData(" espace")]
    [InlineData("point.")]
    [InlineData("a|b")]
    public void Chemins_refuses(string? nom)
    {
        Assert.False(NelfePlayAgentService.NomSimple(nom));
    }

    [Theory]
    [InlineData("jeu.bat", true)]
    [InlineData("jeu.CMD", true)]
    [InlineData("jeu.ps1", true)]
    [InlineData("jeu.lnk", true)]
    [InlineData("jeu.zip", false)]
    [InlineData("jeu.nes", false)]
    [InlineData("jeu.chd", false)]
    public void Extensions_executables(string nom, bool executable)
    {
        Assert.Equal(executable, NelfePlayAgentService.ExtensionExecutable(nom));
    }
}
