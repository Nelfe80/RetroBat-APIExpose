using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

public class RetroArchWrapperDeploymentRetryTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromSeconds(60);

    [Fact]
    public void The_first_retries_follow_the_configured_interval()
    {
        Assert.Equal(Minute, RetroArchWrapperDeploymentHostedService.DelaiAvantReprise(0, 30, Minute));
        Assert.Equal(Minute, RetroArchWrapperDeploymentHostedService.DelaiAvantReprise(29, 30, Minute));
    }

    [Fact]
    public void Once_the_fast_retries_are_spent_it_keeps_trying_slowly_instead_of_giving_up()
    {
        // Une borne ou l'on joue plus de 30 minutes juste apres une mise a jour gardait l'ancien
        // wrapper jusqu'au prochain redemarrage de l'API.
        Assert.Equal(RetroArchWrapperDeploymentHostedService.RepriseLente,
            RetroArchWrapperDeploymentHostedService.DelaiAvantReprise(30, 30, Minute));
        Assert.Equal(RetroArchWrapperDeploymentHostedService.RepriseLente,
            RetroArchWrapperDeploymentHostedService.DelaiAvantReprise(500, 30, Minute));
    }

    [Fact]
    public void A_configured_interval_longer_than_the_slow_cadence_is_kept()
    {
        var dixMinutes = TimeSpan.FromMinutes(10);

        Assert.Equal(dixMinutes, RetroArchWrapperDeploymentHostedService.DelaiAvantReprise(40, 30, dixMinutes));
    }

    [Fact]
    public void No_fast_retry_configured_goes_straight_to_the_slow_cadence()
    {
        Assert.Equal(RetroArchWrapperDeploymentHostedService.RepriseLente,
            RetroArchWrapperDeploymentHostedService.DelaiAvantReprise(0, 0, Minute));
    }
}
