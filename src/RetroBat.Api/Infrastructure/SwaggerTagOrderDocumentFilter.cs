using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Orders the Swagger UI groups on the manager logic of the EmulationStation
/// menu (status first, internal surfaces last) instead of alphabetically.
/// </summary>
public sealed class SwaggerTagOrderDocumentFilter : IDocumentFilter
{
    private static readonly string[] OrderedTags =
    [
        "System & Health",
        "Context & Navigation",
        "Local Media Manager",
        "Auto Scraping Manager",
        "Roms Manager",
        "Control Panel Manager",
        "Marquee Manager",
        "Themes & Collections",
        "Game Events",
        "Notifications & UI",
        "Real-time (WebSocket)",
        "Internal & Prototype"
    ];

    public void Apply(OpenApiDocument document, DocumentFilterContext context)
    {
        var usedTags = document.Paths.Values
            .SelectMany(path => path.Operations.Values)
            .SelectMany(operation => operation.Tags ?? [])
            .Select(tag => tag.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        document.Tags = usedTags
            .OrderBy(name =>
            {
                var index = Array.FindIndex(OrderedTags, tag => string.Equals(tag, name, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? OrderedTags.Length : index;
            })
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(name => new OpenApiTag { Name = name })
            .ToList();
    }
}
