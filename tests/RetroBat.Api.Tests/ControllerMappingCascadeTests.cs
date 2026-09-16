using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// La cascade qui retrouve le mappage d'une manette dans gamecontrollerdb.txt. Les GUID sont
/// ceux que donne le SDL 2.0.14 de RetroArch : une manette Xbox ouverte par XInput n'a ni
/// constructeur ni produit, seulement « xinput » en hexadecimal.
/// </summary>
public class ControllerMappingCascadeTests
{
    private static readonly IReadOnlyList<string[]> Base = new[]
    {
        "030000005e0400008e02000000000000,Xbox 360 Controller,a:b0,b:b1".Split(','),
        "030000004c050000cc09000000000000,PS4 Controller,a:b1,b:b2".Split(','),
        "03000000790000000600000000000000,Generic USB Joystick,a:b2,b:b1".Split(','),
        "xinput,XInput Controller,a:b0,b:b1,x:b2,y:b3".Split(','),
    };

    [Fact]
    public void Une_manette_xinput_prend_la_ligne_xinput()
    {
        var (ligne, source) = CabinetInputReader.FindDbEntry(Base, "78696e70757401000000000000000000");

        Assert.NotNull(ligne);
        Assert.Equal("xinput", ligne![0]);
        Assert.Equal("xinput", source);
    }

    [Fact]
    public void Le_guid_exact_passe_en_premier()
    {
        var (ligne, source) = CabinetInputReader.FindDbEntry(Base, "03000000790000000600000000000000");

        Assert.Equal("Generic USB Joystick", ligne![1]);
        Assert.Equal("GUID exact", source);
    }

    [Theory]
    [InlineData("030000004c050000cc09000000006800", "PS4 Controller")]
    [InlineData("030000005e0400008e02000000007200", "Xbox 360 Controller")]
    public void Un_suffixe_de_pilote_retombe_sur_constructeur_et_produit(string guid, string attendu)
    {
        var (ligne, source) = CabinetInputReader.FindDbEntry(Base, guid);

        Assert.Equal(attendu, ligne![1]);
        Assert.Equal("constructeur+produit", source);
    }

    [Fact]
    public void Une_manette_inconnue_ne_trouve_rien()
    {
        var (ligne, _) = CabinetInputReader.FindDbEntry(Base, "03000000ffff0000eeee000000000000");

        Assert.Null(ligne);
    }
}
