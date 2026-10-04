namespace RetroBat.Domain.Services;

/// <summary>
/// Lit les sources de médias du scraper intégré d'EmulationStation (<c>box-2D</c>, <c>wheel</c>,
/// <c>mixrbv2</c>, les noms de médias ScreenScraper) dans le vocabulaire d'APIExpose (<c>box2d</c>,
/// <c>logo</c>, <c>mix</c>), pour reprendre les choix d'une installation qui n'avait qu'ES.
///
/// Dans ce sens seulement. Les clés natives d'ES (<c>ScrapperImageSrc</c>, <c>ScrapperLogoSrc</c>,
/// <c>ScrapperThumbSrc</c>) sont au joueur : APIExpose les lit, il ne les écrit plus. Il y recopiait
/// sa répartition des médias, ce qui remettait les choix de scrap du joueur à chaque lancement de
/// RetroBat (signalé par un testeur le 2026-10-04).
/// </summary>
public static class EmulationStationScraperVocabulary
{
    private static readonly Dictionary<string, string> FromEs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["box-2D"] = "box2d",
        ["box-3D"] = "box3d",
        ["mixrbv2"] = "mix",
        ["wheel"] = "logo",
    };

    /// <summary>Le choix d'APIExpose qui correspond à une valeur des clés natives d'ES.</summary>
    public static string? FromEmulationStation(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        return FromEs.TryGetValue(normalized, out var api) ? api : normalized;
    }
}
