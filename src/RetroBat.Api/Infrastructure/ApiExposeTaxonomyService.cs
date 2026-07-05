using Microsoft.Extensions.Options;

namespace RetroBat.Api.Infrastructure;

public sealed class ApiExposeTaxonomyService
{
    private readonly IOptionsMonitor<ApiExposeOptions> _options;

    public ApiExposeTaxonomyService(IOptionsMonitor<ApiExposeOptions> options)
    {
        _options = options;
    }

    public IReadOnlyList<string> BuildRomRegionPriority(string profile)
    {
        var preferred = ResolveRegion(profile)?.RomValue ?? "World";
        return Regions()
            .OrderBy(region => string.Equals(region.RomValue, preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(region => DefaultRegionOrderIndex(region.Key))
            .Select(region => region.RomValue)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> BuildRomLanguagePriority(string profile)
    {
        var normalizedProfile = NormalizeKey(profile);
        if (normalizedProfile is "multilingual" or "multi")
        {
            return ["Multi", "Fr", "En", "Ja", "De", "Es", "It", "Pt", "Ko", "Zh", "Ru", "Nl", "Sv", "Pl", "Da", "Fi"];
        }

        var preferred = ResolveLanguage(profile)?.Code ?? "En";
        return new[] { preferred, "Multi" }
            .Concat(Languages()
            .OrderBy(language => string.Equals(language.Code, preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(language => DefaultLanguageOrderIndex(language.Code))
            .Select(language => language.Code))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string NormalizeRomRegionToken(string value)
    {
        return ResolveRegion(value)?.RomValue ?? string.Empty;
    }

    public IReadOnlyList<string> NormalizeRomLanguageTokens(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Array.Empty<string>();
        }

        if (string.Equals(NormalizeKey(normalized), "multi", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeKey(normalized), "multilingual", StringComparison.OrdinalIgnoreCase))
        {
            return ["Multi"];
        }

        var result = new List<string>();
        foreach (var token in normalized.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var language = ResolveLanguage(token);
            if (language != null && !result.Contains(language.Code, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(language.Code);
            }
        }

        return result;
    }

    public string ResolvePreferredScreenScraperRegion(string configuredRegion, string language)
    {
        var configured = NormalizeKey(configuredRegion);
        if (!string.IsNullOrWhiteSpace(configured) &&
            configured != "auto" &&
            configured != "ss" &&
            configured != "screenscraper" &&
            configured != "screen_scraper")
        {
            return ResolveRegion(configured)?.ScreenScraperCode ?? configured;
        }

        var resolvedLanguage = ResolveLanguage(language);
        if (!string.IsNullOrWhiteSpace(resolvedLanguage?.DefaultScreenScraperRegion))
        {
            return resolvedLanguage.DefaultScreenScraperRegion;
        }

        return "wor";
    }

    public IReadOnlyList<string> BuildAllScreenScraperRegions(string preferredRegion)
    {
        var configured = _options.CurrentValue.Taxonomy.ScreenScraperRegionOrder;
        var regions = configured.Count > 0
            ? configured
            : ["eu", "us", "jp", "wor", "br", "kr", "au", "ss", "asi", "cn"];
        return regions
            .Prepend(preferredRegion)
            .Select(value => NormalizeScreenScraperRegionCode(value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string NormalizeScreenScraperRegionCode(string value)
    {
        var region = ResolveRegion(value);
        if (region != null)
        {
            return region.ScreenScraperCode;
        }

        return NormalizeKey(value) switch
        {
            "world" => "wor",
            "japan" => "jp",
            "usa" or "united_states" => "us",
            "europe" => "eu",
            "south_korea" or "korea" => "kr",
            "brazil" => "br",
            "australia" => "au",
            "screen_scraper" or "screenscraper" => "ss",
            "asia" => "asi",
            "china" => "cn",
            var code => code
        };
    }

    public string NormalizeOrientation(string value, string fallback = "horizontal")
    {
        return NormalizeKey(value) switch
        {
            "vertical" or "v" => "vertical",
            "cocktail" or "table" => "cocktail",
            "horizontal" or "h" => "horizontal",
            "auto" or "ignore" or "any" or "all" => "ignore",
            _ => fallback
        };
    }

    public string NormalizeOrientationFilter(string value)
    {
        return NormalizeKey(value) switch
        {
            "prefer_horizontal" or "horizontal" or "only_horizontal" => "only_horizontal",
            "prefer_vertical" or "vertical" or "only_vertical" => "only_vertical",
            "cocktail" or "only_cocktail" => "only_cocktail",
            "hide_cocktail" => "hide_cocktail",
            "auto" or "ignore" => "ignore",
            _ => "ignore"
        };
    }

    public string ResolveBezelOrientation(string bezelOrientation, string cabinetOrientation)
    {
        var normalized = NormalizeKey(bezelOrientation);
        if (normalized is "match_cabinet" or "auto" or "from_cabinet")
        {
            return NormalizeOrientationFilter(cabinetOrientation) switch
            {
                "only_vertical" => "vertical",
                "only_cocktail" => "cocktail",
                _ => "horizontal"
            };
        }

        return NormalizeOrientation(bezelOrientation, "horizontal") switch
        {
            "vertical" => "vertical",
            "cocktail" => "cocktail",
            _ => "horizontal"
        };
    }

    private RegionDefinition? ResolveRegion(string value)
    {
        var normalized = NormalizeKey(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return Regions().FirstOrDefault(region =>
            string.Equals(NormalizeKey(region.Key), normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeKey(region.Label), normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeKey(region.RomValue), normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeKey(region.ScreenScraperCode), normalized, StringComparison.OrdinalIgnoreCase) ||
            region.Aliases.Any(alias => string.Equals(NormalizeKey(alias), normalized, StringComparison.OrdinalIgnoreCase)));
    }

    private LanguageDefinition? ResolveLanguage(string value)
    {
        var normalized = NormalizeKey(value).Replace("_", "-", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return Languages().FirstOrDefault(language =>
            string.Equals(NormalizeKey(language.Key), normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeKey(language.Label), normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeKey(language.Code), normalized, StringComparison.OrdinalIgnoreCase) ||
            language.Aliases.Any(alias => string.Equals(NormalizeKey(alias).Replace("_", "-", StringComparison.Ordinal), normalized, StringComparison.OrdinalIgnoreCase)));
    }

    private IReadOnlyList<RegionDefinition> Regions()
    {
        var configured = _options.CurrentValue.Taxonomy.Regions
            .Where(region => !string.IsNullOrWhiteSpace(region.Key))
            .Select(region => new RegionDefinition(
                region.Key,
                string.IsNullOrWhiteSpace(region.Label) ? region.Key : region.Label,
                string.IsNullOrWhiteSpace(region.RomValue) ? region.Key : region.RomValue,
                string.IsNullOrWhiteSpace(region.ScreenScraperCode) ? region.Key : region.ScreenScraperCode,
                region.Aliases ?? new List<string>()))
            .ToList();

        return configured.Count > 0 ? configured : DefaultRegions;
    }

    private IReadOnlyList<LanguageDefinition> Languages()
    {
        var configured = _options.CurrentValue.Taxonomy.Languages
            .Where(language => !string.IsNullOrWhiteSpace(language.Key))
            .Select(language => new LanguageDefinition(
                language.Key,
                string.IsNullOrWhiteSpace(language.Code) ? language.Key : language.Code,
                string.IsNullOrWhiteSpace(language.Label) ? language.Key : language.Label,
                language.DefaultScreenScraperRegion,
                language.Aliases ?? new List<string>()))
            .ToList();

        return configured.Count > 0 ? configured : DefaultLanguages;
    }

    private static string NormalizeKey(string value)
    {
        return (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace('-', '_')
            .Replace(' ', '_');
    }

    private static int DefaultRegionOrderIndex(string key)
    {
        var normalized = NormalizeKey(key);
        var index = DefaultRegionOrder.FindIndex(value => string.Equals(value, normalized, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? DefaultRegionOrder.Count + 1 : index;
    }

    private static int DefaultLanguageOrderIndex(string code)
    {
        var index = DefaultLanguageOrder.FindIndex(value => string.Equals(value, code, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? DefaultLanguageOrder.Count + 1 : index;
    }

    private sealed record RegionDefinition(string Key, string Label, string RomValue, string ScreenScraperCode, IReadOnlyList<string> Aliases);

    private sealed record LanguageDefinition(string Key, string Code, string Label, string DefaultScreenScraperRegion, IReadOnlyList<string> Aliases);

    private static readonly List<string> DefaultRegionOrder =
    [
        "world", "europe", "usa", "japan", "france", "asia", "korea", "australia", "brazil", "china", "taiwan",
        "germany", "italy", "spain", "sweden", "netherlands", "russia", "canada", "united_kingdom", "oceania"
    ];

    private static readonly List<string> DefaultLanguageOrder =
    [
        "En", "Fr", "Ja", "De", "Es", "It", "Pt", "Ko", "Zh", "Ru", "Nl", "Sv", "Pl", "Da", "Fi"
    ];

    private static readonly IReadOnlyList<RegionDefinition> DefaultRegions =
    [
        new("world", "World", "World", "wor", ["w", "ww", "global"]),
        new("europe", "Europe", "Europe", "eu", ["eur", "pal"]),
        new("usa", "USA", "USA", "us", ["us", "u", "america", "north america", "united states", "united_states"]),
        new("japan", "Japan", "Japan", "jp", ["jp", "j"]),
        new("france", "France", "France", "eu", ["fr", "fra"]),
        new("asia", "Asia", "Asia", "asi", ["as"]),
        new("korea", "Korea", "Korea", "kr", ["kr", "ko", "south korea", "south_korea"]),
        new("australia", "Australia", "Australia", "au", ["aus"]),
        new("brazil", "Brazil", "Brazil", "br", ["bra"]),
        new("china", "China", "China", "cn", ["cn", "zh"]),
        new("taiwan", "Taiwan", "Taiwan", "cn", ["tw"]),
        new("germany", "Germany", "Germany", "eu", ["de", "ger", "deu"]),
        new("italy", "Italy", "Italy", "eu", ["it", "ita"]),
        new("spain", "Spain", "Spain", "eu", ["es", "spa"]),
        new("sweden", "Sweden", "Sweden", "eu", ["se", "sv", "swe"]),
        new("netherlands", "Netherlands", "Netherlands", "eu", ["nl", "dutch"]),
        new("russia", "Russia", "Russia", "eu", ["ru", "rus"]),
        new("canada", "Canada", "Canada", "us", ["ca"]),
        new("united_kingdom", "United Kingdom", "United Kingdom", "eu", ["uk", "gb", "england"]),
        new("oceania", "Oceania", "Oceania", "au", [])
    ];

    private static readonly IReadOnlyList<LanguageDefinition> DefaultLanguages =
    [
        new("english", "En", "English", "us", ["en", "eng", "en-us", "en-gb", "uk"]),
        new("french", "Fr", "French", "eu", ["fr", "fre", "fra", "fr-fr", "france"]),
        new("japanese", "Ja", "Japanese", "jp", ["ja", "jp", "jpn", "ja-jp"]),
        new("german", "De", "German", "eu", ["de", "ger", "deu", "de-de"]),
        new("spanish", "Es", "Spanish", "eu", ["es", "spa", "es-es"]),
        new("italian", "It", "Italian", "eu", ["it", "ita", "it-it"]),
        new("portuguese", "Pt", "Portuguese", "br", ["pt", "por", "pt-br"]),
        new("korean", "Ko", "Korean", "kr", ["ko", "kor", "kr"]),
        new("chinese", "Zh", "Chinese", "cn", ["zh", "chi", "zho", "cn"]),
        new("russian", "Ru", "Russian", "eu", ["ru", "rus"]),
        new("dutch", "Nl", "Dutch", "eu", ["nl", "dut", "nld"]),
        new("swedish", "Sv", "Swedish", "eu", ["sv", "swe"]),
        new("polish", "Pl", "Polish", "eu", ["pl", "pol"]),
        new("danish", "Da", "Danish", "eu", ["da", "dan"]),
        new("finnish", "Fi", "Finnish", "eu", ["fi", "fin"])
    ];
}
