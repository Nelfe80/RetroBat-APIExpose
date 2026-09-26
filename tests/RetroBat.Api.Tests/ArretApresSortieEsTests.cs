using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using RetroBat.Api.Controllers;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// 2026-09-26 : ES relance 3 s apres sa fermeture. Son script de demarrage a trouve l'API « prete »
/// alors qu'elle etait en train de s'arreter, ne l'a pas relancee, et la borne est restee sans API.
/// Une API qui s'arrete ne se dit plus prete ni en bonne sante : le script la remplace.
/// </summary>
public class ArretApresSortieEsTests
{
    [Fact]
    public void Une_api_qui_s_arrete_ne_se_dit_plus_prete()
    {
        var etat = new StartupReadinessState();
        etat.MarkReady();
        Assert.IsType<OkObjectResult>(new StartupController(etat).Ready().Result);

        etat.MarquerArret();
        var reponse = Assert.IsType<ObjectResult>(new StartupController(etat).Ready().Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, reponse.StatusCode);
        Assert.DoesNotContain("\"ready\":true", JsonSerializer.Serialize(reponse.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void Une_api_qui_s_arrete_ne_dit_pas_healthy()
    {
        // Le script cherche le mot « healthy » (find /I) : « unhealthy » le contiendrait.
        var etat = new StartupReadinessState();
        etat.MarquerArret();

        var reponse = Assert.IsType<ObjectResult>(new HealthController(etat).Get().Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, reponse.StatusCode);
        Assert.DoesNotContain("healthy", JsonSerializer.Serialize(reponse.Value), System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void En_service_rien_ne_change()
        => Assert.IsType<OkObjectResult>(new HealthController(new StartupReadinessState()).Get().Result);
}
