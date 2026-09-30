using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using Xunit;

namespace RetroBat.Api.Tests;

/// <summary>
/// Les tables de regions et de langues sont construites une fois par configuration (2026-09-30) :
/// la recherche doit rendre EXACTEMENT ce que rendait l'ancien parcours, recopie ici comme reference.
/// </summary>
public sealed class TaxonomyCacheTests
{
    private static ApiExposeOptions Options(bool autre = false)
    {
        var options = new ApiExposeOptions();
        options.Taxonomy.Regions =
        [
            new() { Key = "europe", Label = "Europe", RomValue = "Europe", ScreenScraperCode = "eu", Aliases = ["pal", "eur"] },
            // Doublons voulus : le premier de la liste doit gagner, comme avant.
            new() { Key = "france", Label = "France", RomValue = "France", ScreenScraperCode = "eu", Aliases = ["fr", "pal"] },
            new() { Key = "usa", Label = "USA", RomValue = autre ? "US" : "USA", ScreenScraperCode = "us", Aliases = ["united states", "north-america"] },
            new() { Key = "world", Label = "", RomValue = "", ScreenScraperCode = "", Aliases = [] },
        ];
        options.Taxonomy.Languages =
        [
            new() { Key = "english", Code = "En", Label = "English", Aliases = ["en", "en_us", "eng"] },
            new() { Key = "french", Code = "Fr", Label = "French", Aliases = ["fr", "fr_fr", "en"] },
            new() { Key = "pt-br", Code = "Pt", Label = "Portugues", Aliases = ["pt_br"] },
        ];
        return options;
    }

    private static readonly string[] Entrees =
    [
        "europe", "EUROPE", " Europe ", "eu", "pal", "eur", "france", "fr", "FR", "usa", "us", "united states",
        "United_States", "north america", "north-america", "north_america", "world", "wor", "english", "en", "En",
        "eng", "en_us", "en-us", "en us", "french", "fr_fr", "fr-fr", "pt-br", "pt_br", "pt br", "ptbr", "inconnu", "",
    ];

    [Fact]
    public void LesRegionsRendentCeQueRendaitLAncienParcours()
    {
        var options = Options();
        var service = new ApiExposeTaxonomyService(new FauxOptions(options));

        foreach (var entree in Entrees)
        {
            Assert.Equal(AncienneRegion(options, entree), service.NormalizeRomRegionToken(entree));
        }
    }

    [Fact]
    public void LesLanguesRendentCeQueRendaitLAncienParcours()
    {
        var options = Options();
        var service = new ApiExposeTaxonomyService(new FauxOptions(options));

        foreach (var entree in Entrees)
        {
            Assert.Equal(AnciennesLangues(options, entree), service.NormalizeRomLanguageTokens(entree));
        }
    }

    [Fact]
    public void UneConfigurationRechargeeEstRelue()
    {
        var faux = new FauxOptions(Options());
        var service = new ApiExposeTaxonomyService(faux);
        Assert.Equal("USA", service.NormalizeRomRegionToken("united states"));

        faux.CurrentValue = Options(autre: true);

        Assert.Equal("US", service.NormalizeRomRegionToken("united states"));
    }

    [Fact]
    public void SansConfigurationLesTablesParDefautServent()
    {
        var service = new ApiExposeTaxonomyService(new FauxOptions(new ApiExposeOptions()));

        Assert.Equal("Europe", service.NormalizeRomRegionToken("eu"));
        Assert.Equal("Europe", service.NormalizeRomRegionToken("pal"));
        Assert.Equal("France", service.NormalizeRomRegionToken("fr"));
        Assert.Equal("USA", service.NormalizeRomRegionToken("united states"));
        Assert.Equal(["En", "Fr"], service.NormalizeRomLanguageTokens("en-fr"));
    }

    // ── l'ancien parcours, recopie tel quel ─────────────────────────────────

    private static string Cle(string? v) => (v ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    private static string AncienneRegion(ApiExposeOptions options, string value)
    {
        var normalized = Cle(value);
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;
        var region = options.Taxonomy.Regions
            .Where(r => !string.IsNullOrWhiteSpace(r.Key))
            .Select(r => (Key: r.Key,
                Label: string.IsNullOrWhiteSpace(r.Label) ? r.Key : r.Label,
                RomValue: string.IsNullOrWhiteSpace(r.RomValue) ? r.Key : r.RomValue,
                Code: string.IsNullOrWhiteSpace(r.ScreenScraperCode) ? r.Key : r.ScreenScraperCode,
                r.Aliases))
            .FirstOrDefault(r =>
                string.Equals(Cle(r.Key), normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Cle(r.Label), normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Cle(r.RomValue), normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Cle(r.Code), normalized, StringComparison.OrdinalIgnoreCase) ||
                r.Aliases.Any(a => string.Equals(Cle(a), normalized, StringComparison.OrdinalIgnoreCase)));
        return region.Key is null ? string.Empty : region.RomValue;
    }

    private static IReadOnlyList<string> AnciennesLangues(ApiExposeOptions options, string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized)) return [];
        if (Cle(normalized) is "multi" or "multilingual") return ["Multi"];

        var langues = options.Taxonomy.Languages
            .Where(l => !string.IsNullOrWhiteSpace(l.Key))
            .Select(l => (Key: l.Key,
                Code: string.IsNullOrWhiteSpace(l.Code) ? l.Key : l.Code,
                Label: string.IsNullOrWhiteSpace(l.Label) ? l.Key : l.Label,
                l.Aliases))
            .ToList();
        var result = new List<string>();
        foreach (var token in normalized.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cherche = Cle(token).Replace("_", "-", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(cherche)) continue;
            var langue = langues.FirstOrDefault(l =>
                string.Equals(Cle(l.Key), cherche, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Cle(l.Label), cherche, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Cle(l.Code), cherche, StringComparison.OrdinalIgnoreCase) ||
                l.Aliases.Any(a => string.Equals(Cle(a).Replace("_", "-", StringComparison.Ordinal), cherche, StringComparison.OrdinalIgnoreCase)));
            if (langue.Key is not null && !result.Contains(langue.Code, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(langue.Code);
            }
        }
        return result;
    }

    private sealed class FauxOptions(ApiExposeOptions valeur) : IOptionsMonitor<ApiExposeOptions>
    {
        public ApiExposeOptions CurrentValue { get; set; } = valeur;

        public ApiExposeOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ApiExposeOptions, string?> listener) => null;
    }
}
