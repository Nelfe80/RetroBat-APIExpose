namespace RetroBat.Domain.Services;

/// <summary>
/// Traduit les sources de médias entre le vocabulaire d'APIExpose (<c>box2d</c>, <c>logo</c>,
/// <c>mix</c>) et celui du scraper intégré d'EmulationStation (<c>box-2D</c>, <c>wheel</c>,
/// <c>mixrbv2</c>), qui sont les noms de médias ScreenScraper.
///
/// APIExpose recopie ses choix dans les clés natives d'ES (<c>ScrapperImageSrc</c>,
/// <c>ScrapperLogoSrc</c>, <c>ScrapperThumbSrc</c>) pour que le scraper intégré suive les
/// mêmes choix. Recopiés tels quels, <c>box2d</c> et <c>logo</c> ne veulent rien dire pour
/// ES : son menu affichait « NONE » pour BOX SOURCE et LOGO SOURCE (signalé par un testeur le
/// 2026-09-17), et son scraper n'aurait ramené ni boîte ni logo. Un choix sans équivalent chez
/// ES (figurine, cartouche, screen marquee…) laisse la clé d'ES telle quelle.
/// </summary>
public static class EmulationStationScraperVocabulary
{
    public enum Slot
    {
        Image,
        Logo,
        Thumb,
    }

    // APIExpose -> ES. ES n'a que NONE / BOX 2D / BOX 3D pour la boîte, et NONE / WHEEL /
    // WHEEL HD / MARQUEE pour le logo.
    private static readonly Dictionary<string, string> ImageToEs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ss"] = "ss",
        ["sstitle"] = "sstitle",
        ["mixrbv1"] = "mixrbv1",
        ["mix"] = "mixrbv2",
        ["mixrbv2"] = "mixrbv2",
        ["box2d"] = "box-2D",
        ["box3d"] = "box-3D",
        ["fanart"] = "fanart",
    };

    private static readonly Dictionary<string, string> LogoToEs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["logo"] = "wheel",
        ["wheel"] = "wheel",
        ["wheel-hd"] = "wheel-hd",
        ["marquee"] = "marquee",
    };

    private static readonly Dictionary<string, string> ThumbToEs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["box2d"] = "box-2D",
        ["box3d"] = "box-3D",
    };

    // ES -> APIExpose, pour reprendre les choix d'une installation qui n'avait qu'ES.
    private static readonly Dictionary<string, string> FromEs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["box-2D"] = "box2d",
        ["box-3D"] = "box3d",
        ["mixrbv2"] = "mix",
        ["wheel"] = "logo",
    };

    /// <summary>La valeur qu'ES comprend pour ce choix d'APIExpose, ou null s'il n'en a pas.</summary>
    public static string? ToEmulationStation(Slot slot, string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        var table = slot switch
        {
            Slot.Image => ImageToEs,
            Slot.Logo => LogoToEs,
            _ => ThumbToEs,
        };
        return table.TryGetValue(normalized, out var es) ? es : null;
    }

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
