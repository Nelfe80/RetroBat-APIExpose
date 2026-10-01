using System.Text.Json;
using RetroBat.Api.Netplay;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Un defi diffuse sur un jeu absent des journaux d'ES (2026-10-01, Metal Slug 3) : l'hebergement
/// reconstitue le lancement au lieu de partir sans direct. Le systeme vient du dossier de la ROM,
/// le choix du jeu de sa fiche ES.
/// </summary>
public sealed class NetplayHostReconstitutionTests
{
    [Theory]
    [InlineData(@"E:\RetroBat\roms\fbneo\mslug3.zip", "fbneo")]
    [InlineData(@"E:\RetroBat\roms\psx\Un jeu\disque.cue", "psx")]
    [InlineData("\"E:\\RetroBat\\roms\\mame\\19xx.zip\"", "mame")]
    [InlineData(@"E:/RetroBat/roms/megadrive/sonic.zip", "megadrive")]
    [InlineData(@"E:\RetroBat\roms\perdu.zip", "")]
    [InlineData(@"D:\ailleurs\fbneo\mslug3.zip", "")]
    public void LeSystemeEstLePremierDossierSousRoms(string rom, string attendu)
        => Assert.Equal(attendu, NetplayHostService.SystemeDuChemin(rom, @"E:\RetroBat\roms"));

    [Fact]
    public void LaFicheDuJeuDonneSonEmulateurEtSonCoeur()
    {
        using var doc = JsonDocument.Parse("""
            [
              { "path": "./1942.zip", "emulator": "libretro", "core": "fbneo" },
              { "path": "./mslug3.zip", "emulator": "libretro", "core": "fbneo" },
              { "path": "./sonic.zip" }
            ]
            """);

        Assert.Equal(("libretro", "fbneo"), NetplayHostService.FicheDans(doc.RootElement, @"E:\RetroBat\roms\fbneo\MSLUG3.zip"));
        Assert.Equal(((string?)null, (string?)null), NetplayHostService.FicheDans(doc.RootElement, @"E:\RetroBat\roms\fbneo\sonic.zip"));
        Assert.Equal(((string?)null, (string?)null), NetplayHostService.FicheDans(doc.RootElement, @"E:\RetroBat\roms\fbneo\absent.zip"));
    }
}
