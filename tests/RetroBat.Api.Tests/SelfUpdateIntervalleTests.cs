using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

public sealed class SelfUpdateIntervalleTests
{
    [Theory]
    // L'ancien defaut, reste ecrit dans le appsettings.json des bornes (jamais remplace par une mise a jour) : 1 h.
    [InlineData(6, 1)]
    [InlineData(1, 1)]
    // 0 ou moins : une seule verification ; si la borne etait occupee, on repasse dans l'heure.
    [InlineData(0, 1)]
    [InlineData(-3, 1)]
    // Une valeur choisie a la main est gardee.
    [InlineData(2, 2)]
    [InlineData(12, 12)]
    public void La_mise_a_jour_se_verifie_toutes_les_heures(int configure, int attendu)
    {
        Assert.Equal(attendu, SelfUpdateHostedService.IntervalleEffectif(configure));
    }

    [Fact]
    public void Le_defaut_des_options_est_d_une_heure()
    {
        Assert.Equal(1, new ApiExposeOptions.SelfUpdateOptions().IntervalHours);
    }
}
