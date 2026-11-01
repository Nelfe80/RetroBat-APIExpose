using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>Un SDL d'ES qui n'est pas en 64 bits : l'API prend celui de RetroArch, sinon aucune manette n'est lue (31/10).</summary>
public sealed class RepliSdlTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "nelfeplay-sdl-" + Guid.NewGuid().ToString("N"));

    public RepliSdlTests() => Directory.CreateDirectory(Path.Combine(_racine, "emulationstation"));

    public void Dispose() => Directory.Delete(_racine, recursive: true);

    private string Es => Path.Combine(_racine, "emulationstation", "SDL2.dll");

    /// <summary>Le debut d'une DLL : MZ, l'adresse de l'en-tete PE, sa signature, son processeur.</summary>
    private static byte[] Dll(ushort machine)
    {
        var octets = new byte[0x100];
        octets[0] = (byte)'M';
        octets[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(octets, 0x3C);
        new byte[] { (byte)'P', (byte)'E', 0, 0 }.CopyTo(octets, 0x80);
        BitConverter.GetBytes(machine).CopyTo(octets, 0x84);
        return octets;
    }

    [Fact]
    public void Un_SDL_d_ES_en_32_bits_passe_la_main_a_celui_de_RetroArch()
    {
        File.WriteAllBytes(Es, Dll(0x014C));

        var (chemin, origine) = CabinetInputReader.ResoudreSdl(_racine, Path.Combine(_racine, "copie"));

        Assert.Equal(Path.Combine(_racine, "emulators", "retroarch", "SDL2.dll"), chemin);
        Assert.Contains("inutilisable", origine);
    }

    [Fact]
    public void Un_SDL_d_ES_en_64_bits_reste_celui_de_l_API()
    {
        File.WriteAllBytes(Es, Dll(0x8664));

        var (chemin, origine) = CabinetInputReader.ResoudreSdl(_racine, Path.Combine(_racine, "copie"));

        Assert.Equal(Path.Combine(_racine, "copie", "SDL2.dll"), chemin);
        Assert.Equal("EmulationStation", origine);
    }

    [Fact]
    public void Un_fichier_qui_n_est_pas_une_DLL_ne_declenche_pas_le_repli()
    {
        File.WriteAllText(Es, "pas une dll");

        Assert.Null(CabinetInputReader.MachineDeLaDll(Es));
        Assert.Equal("EmulationStation", CabinetInputReader.ResoudreSdl(_racine, Path.Combine(_racine, "copie")).Origine);
    }
}
