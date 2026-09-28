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

    [Theory]
    [InlineData("03000000790000007c18000000007801")]   // Pro Fight, stick arcade XInput absent de la base
    [InlineData("030000005e0400008e02000000007801")]   // manette Xbox 360 par XInput
    public void Une_manette_ouverte_par_xinput_prend_la_ligne_xinput(string guid)
    {
        var (ligne, source) = CabinetInputReader.FindDbEntry(Base, guid);

        Assert.Equal("xinput", ligne![0]);
        Assert.Equal("xinput", source);
    }

    [Theory]
    [InlineData("03000000790000007c18000000007200", false)]   // le meme stick vu par RawInput (ES)
    [InlineData("030000004c050000cc09000000006800", false)]   // HIDAPI
    [InlineData("03000000790000000600000000000000", false)]   // DirectInput
    [InlineData("03000000790000007c18000000007801", true)]
    public void Seul_le_marqueur_x_designe_xinput(string guid, bool attendu)
        => Assert.Equal(attendu, CabinetInputReader.OuvertParXInput(guid));

    [Theory]
    [InlineData("a", "b")]   // le bouton du bas, qui valide
    [InlineData("b", "a")]
    [InlineData("x", "x")]
    [InlineData("y", "y")]
    [InlineData("start", "start")]
    public void Es_input_nomme_les_boutons_comme_la_base(string nomEs, string identite)
        => Assert.Equal(identite, CabinetInputReader.IdentiteDuNomEs(nomEs));

    [Fact]
    public void Une_manette_inconnue_ne_trouve_rien()
    {
        var (ligne, _) = CabinetInputReader.FindDbEntry(Base, "03000000ffff0000eeee000000000000");

        Assert.Null(ligne);
    }
}

/// <summary>
/// Le SDL d'EmulationStation (2.32) remplace celui de RetroArch (2.0.14) : GUID avec CRC, copie
/// locale de la DLL (2026-09-28).
/// </summary>
public class SdlEmulationStationTests
{
    [Theory]
    [InlineData("0300a1b2790000007c18000000007801", "03000000790000007c18000000007801")]
    [InlineData("050012ef4c050000cc09000000006803", "050000004c050000cc09000000006803")]
    [InlineData("03000000790000000600000000000000", "03000000790000000600000000000000")]
    [InlineData("78696e70757401000000000000000000", "78696e70757401000000000000000000")]   // « xinput »
    public void La_somme_de_controle_ne_compte_pas(string guid, string attendu)
        => Assert.Equal(attendu, CabinetInputReader.SansCrc(guid));

    [Fact]
    public void Le_sdl_d_emulationstation_est_copie_puis_charge()
    {
        var racine = Path.Combine(Path.GetTempPath(), "rb-sdl-" + Guid.NewGuid().ToString("N"));
        var copies = Path.Combine(racine, "copie");
        try
        {
            Directory.CreateDirectory(Path.Combine(racine, "emulationstation"));
            File.WriteAllText(Path.Combine(racine, "emulationstation", "SDL2.dll"), "sdl d'es");
            var (chemin, origine) = CabinetInputReader.ResoudreSdl(racine, copies);
            Assert.Equal(Path.Combine(copies, "SDL2.dll"), chemin);
            Assert.Equal("EmulationStation", origine);
            Assert.Equal("sdl d'es", File.ReadAllText(chemin));
        }
        finally
        {
            try { Directory.Delete(racine, true); } catch { }
        }
    }

    [Fact]
    public void Sans_sdl_d_emulationstation_on_garde_celui_de_retroarch()
    {
        var racine = Path.Combine(Path.GetTempPath(), "rb-sdl-" + Guid.NewGuid().ToString("N"));
        var (chemin, origine) = CabinetInputReader.ResoudreSdl(racine, Path.Combine(racine, "copie"));
        Assert.Equal(Path.Combine(racine, "emulators", "retroarch", "SDL2.dll"), chemin);
        Assert.Equal("RetroArch", origine);
    }
}
