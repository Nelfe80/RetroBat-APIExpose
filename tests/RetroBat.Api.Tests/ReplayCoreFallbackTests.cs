using RetroBat.Api.Replay.Playback;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Le repli sur le core usuel d'un systeme. Ce qui se teste ici est la TABLE et la
/// normalisation, pas le disque : les manifestes de la borne portent « mega_drive » et
/// « fb_alpha » la ou le dossier d'EmulationStation dit « megadrive » et « fbneo », et une
/// table qui ne connaitrait qu'une seule de ces deux ecritures ne trouverait rien.
/// </summary>
public class ReplayCoreFallbackTests
{
    [Theory]
    [InlineData("mega_drive", null, "genesis_plus_gx")]
    [InlineData("mega_drive", "megadrive", "genesis_plus_gx")]
    [InlineData(null, "megadrive", "genesis_plus_gx")]
    [InlineData("Mega-Drive", null, "genesis_plus_gx")]
    [InlineData("fb_alpha", "fbneo", "fbneo")]
    [InlineData("mame", "mame", "mame")]
    [InlineData("snes", null, "snes9x")]
    public void Le_premier_core_usuel_correspond_au_systeme(string? systemId, string? systemFolder, string attendu)
    {
        var candidats = ReplayRuntimeResolver.CandidatsPourSysteme(systemId, systemFolder);

        Assert.NotEmpty(candidats);
        Assert.Equal(attendu, candidats[0]);
    }

    [Fact]
    public void Le_dossier_d_emulationstation_prime_sur_l_identifiant()
    {
        // Un identifiant inconnu ne doit pas masquer un dossier reconnu.
        var candidats = ReplayRuntimeResolver.CandidatsPourSysteme("systeme_maison", "megadrive");

        Assert.Equal("genesis_plus_gx", candidats[0]);
    }

    [Fact]
    public void Un_systeme_inconnu_ne_propose_aucun_core()
    {
        Assert.Empty(ReplayRuntimeResolver.CandidatsPourSysteme("console_inventee", "dossier_inconnu"));
        Assert.Empty(ReplayRuntimeResolver.CandidatsPourSysteme(null, null));
        Assert.Empty(ReplayRuntimeResolver.CandidatsPourSysteme("   ", string.Empty));
    }

    [Theory]
    [InlineData("mega_drive", "megadrive")]
    [InlineData("Mega-Drive", "megadrive")]
    [InlineData("  MAME  ", "mame")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void La_normalisation_retire_ponctuation_et_casse(string? valeur, string? attendu)
    {
        Assert.Equal(attendu, ReplayRuntimeResolver.Normaliser(valeur));
    }
}
