using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// « PRESERVE CUSTOM MEDIA » est actif par defaut (decision user 2026-09-28) : le gestionnaire de
/// medias ne remplace jamais un media que le joueur a choisi, en mode « fill_missing ».
/// </summary>
public sealed class PreserveCustomMediaTests
{
    [Fact]
    public void Le_mode_protege_est_actif_par_defaut()
    {
        var options = new ApiExposeOptions();
        Assert.True(options.MediaAllocation.WritePolicyEnabled);
        Assert.Equal("fill_missing", options.MediaAllocation.WritePolicy);
    }
}

/// <summary>Le profil de medias choisi a l'installation (neuf, configure, garder).</summary>
public sealed class ProfilInstallationTests
{
    [Theory]
    [InlineData("neuf", true, false, true)]
    [InlineData("NEUF", true, false, true)]
    [InlineData("configure", false, true, false)]
    public void Un_profil_donne_ses_trois_reglages(string profil, bool autoScrap, bool preserve, bool gestionnaire)
    {
        var r = InstallProfileHostedService.Reglages(profil);
        Assert.NotNull(r);
        Assert.Equal(autoScrap, r!.Value.AutoScrap);
        Assert.Equal(preserve, r.Value.Preserve);
        Assert.Equal(gestionnaire, r.Value.Gestionnaire);
    }

    [Fact]
    public void Le_gestionnaire_de_medias_est_coupe_par_defaut()
    {
        // Decision user 2026-10-05 : APIExpose ne touche aux gamelists que si le joueur le demande.
        Assert.False(new ApiExposeOptions().LocalMediaManager.Enabled);
    }

    [Theory]
    [InlineData("garder")]
    [InlineData("")]
    [InlineData(null)]
    public void Garder_ne_change_rien(string? profil)
        => Assert.Null(InstallProfileHostedService.Reglages(profil));

    [Fact]
    public void Le_fichier_de_l_installeur_se_lit()
    {
        var chemin = Path.Combine(Path.GetTempPath(), "install-profile-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(chemin, "{\"media_profile\":\"configure\",\"source\":\"installer\"}");
            Assert.Equal("configure", InstallProfileHostedService.LireProfil(chemin));
            Assert.Null(InstallProfileHostedService.LireProfil(chemin + ".absent"));
        }
        finally
        {
            File.Delete(chemin);
        }
    }
}
