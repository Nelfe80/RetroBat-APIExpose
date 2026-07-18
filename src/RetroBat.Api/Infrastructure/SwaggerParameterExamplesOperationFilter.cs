using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Fills path/query parameters with working example values so "Try it out"
/// returns real data on a stock RetroBat install (megadrive/sonic for
/// consoles, mame/1943 for arcade) instead of empty 200s.
/// </summary>
public sealed class SwaggerParameterExamplesOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation.Parameters is null || operation.Parameters.Count == 0)
        {
            return;
        }

        var route = (context.ApiDescription.RelativePath ?? string.Empty).ToLowerInvariant();
        var arcadeRoute = route.Contains("panels") || route.Contains("mamecfg") ||
            route.Contains("remaps") || route.Contains("fbneormp") || route.Contains("outputs") ||
            route.Contains("hiscores") || route.Contains("mamelua");

        foreach (var parameter in operation.Parameters)
        {
            if (parameter.Example is not null)
            {
                continue;
            }

            var example = parameter.Name.ToLowerInvariant() switch
            {
                "systemid" or "system" => arcadeRoute ? "mame" : "megadrive",
                "rom" => "1943",
                "search" => "sonic",
                "ids" => "1943",
                "game" or "gamename" => "Sonic The Hedgehog",
                "path" when route.StartsWith("api/v1/media") => "systems/megadrive/ui/wheels/wheel.png",
                _ => null
            };

            if (example is not null)
            {
                parameter.Example = new OpenApiString(example);
            }
        }
    }
}
