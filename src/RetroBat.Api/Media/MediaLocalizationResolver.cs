using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Models;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Media;

public sealed class MediaLocalizationResolver
{
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly ApiExposeTaxonomyService _taxonomy;

    public MediaLocalizationResolver(
        IOptionsMonitor<ApiExposeOptions> options,
        EmulationStationSettingsService settingsService,
        ApiExposeTaxonomyService taxonomy)
    {
        _options = options;
        _settingsService = settingsService;
        _taxonomy = taxonomy;
    }

    public IReadOnlyList<string> BuildMediaRegionPriority(MediaProjectionPlan plan, string mediaKind)
    {
        var normalizedKind = MediaKinds.Normalize(mediaKind);
        return IsUserReadableLogoKind(normalizedKind)
            ? BuildLogoRegionPriority(plan)
            : BuildOriginalMediaRegionPriority(plan);
    }

    public IReadOnlyList<string> BuildBezelRegionPriority(MediaProjectionPlan plan)
    {
        return BuildOriginalMediaRegionPriority(plan);
    }

    public bool IsUserReadableLogoKind(string mediaKind)
    {
        var normalizedKind = MediaKinds.Normalize(mediaKind);
        return normalizedKind is
            MediaKinds.Logo or
            MediaKinds.Wheel or
            MediaKinds.WheelCarbon or
            MediaKinds.WheelSteel;
    }

    public static IReadOnlyList<string> InferRomRegions(MediaProjectionPlan plan)
    {
        var regions = new List<string>();
        AddDistinct(regions, plan.RomRegions);
        foreach (var value in BuildPlanTokens(plan))
        {
            AddRomRegionToken(regions, value);
        }

        return regions;
    }

    public static IReadOnlyList<string> InferRomLanguages(MediaProjectionPlan plan)
    {
        var languages = new List<string>();
        AddDistinct(languages, plan.RomLanguages);
        foreach (var value in BuildPlanTokens(plan))
        {
            AddRomLanguageToken(languages, value);
        }

        return languages;
    }

    private IReadOnlyList<string> BuildOriginalMediaRegionPriority(MediaProjectionPlan plan)
    {
        var settings = _settingsService.GetScrapingSettings();
        var regions = new List<string>();
        var mode = NormalizeMediaRegionMode(settings.MediaRegionMode);

        switch (mode)
        {
            case "content_region_profile":
                AddLegacyUserRegion(regions, settings.UserRegion);
                AddRegionCandidates(regions, settings.ContentRegionProfile);
                AddRegionCandidates(regions, _options.CurrentValue.ApiSettings.RegionProfile);
                break;

            case "interface_locale":
                AddLanguageCandidates(regions, settings.Language);
                break;

            default:
                AddRegionCandidates(regions, InferRomRegions(plan));
                AddLanguageCandidates(regions, InferRomLanguages(plan));
                break;
        }

        AddRegionCandidates(regions, settings.ContentRegionProfile);
        AddLanguageCandidates(regions, settings.ContentLanguageProfile);
        AddLanguageCandidates(regions, settings.Language);
        AddRegionCandidates(regions, _options.CurrentValue.ApiSettings.RegionProfile);
        AddLanguageCandidates(regions, _options.CurrentValue.ApiSettings.LanguageProfile);
        return NormalizeRegionPriority(regions);
    }

    private IReadOnlyList<string> BuildLogoRegionPriority(MediaProjectionPlan plan)
    {
        var settings = _settingsService.GetScrapingSettings();
        var regions = new List<string>();
        var mode = NormalizeLogoRegionMode(settings.LogoRegionMode);

        switch (mode)
        {
            case "match_rom_region":
                AddRegionCandidates(regions, InferRomRegions(plan));
                AddLanguageCandidates(regions, InferRomLanguages(plan));
                break;

            case "content_region_profile":
                AddLegacyUserRegion(regions, settings.UserRegion);
                AddRegionCandidates(regions, settings.ContentRegionProfile);
                AddRegionCandidates(regions, _options.CurrentValue.ApiSettings.RegionProfile);
                break;

            default:
                AddLanguageCandidates(regions, settings.ContentLanguageProfile);
                AddLanguageCandidates(regions, settings.Language);
                AddLanguageCandidates(regions, _options.CurrentValue.ApiSettings.LanguageProfile);
                break;
        }

        AddRegionCandidates(regions, settings.ContentRegionProfile);
        AddLanguageCandidates(regions, settings.Language);
        AddRegionCandidates(regions, InferRomRegions(plan));
        return NormalizeRegionPriority(regions);
    }

    private void AddLegacyUserRegion(List<string> target, string value)
    {
        var normalized = NormalizeKey(value);
        if (!string.IsNullOrWhiteSpace(normalized) &&
            normalized is not "auto" and not "ss" and not "screenscraper" and not "screen_scraper")
        {
            AddRegionCandidate(target, normalized);
        }
    }

    private static string NormalizeMediaRegionMode(string value)
    {
        return NormalizeKey(value) switch
        {
            "content_region_profile" or "content_profile" or "region_profile" or "region" or "preferred" => "content_region_profile",
            "interface_locale" or "interface" or "locale" or "ui" or "user_locale" => "interface_locale",
            "all" => "all",
            _ => "match_rom_region"
        };
    }

    private static string NormalizeLogoRegionMode(string value)
    {
        return NormalizeKey(value) switch
        {
            "match_rom_region" or "match_rom" or "rom" or "original" => "match_rom_region",
            "content_region_profile" or "content_profile" or "region_profile" or "region" or "preferred" => "content_region_profile",
            _ => "user_language"
        };
    }

    private IReadOnlyList<string> NormalizeRegionPriority(IEnumerable<string> values)
    {
        var result = new List<string>();
        foreach (var value in values)
        {
            AddRegionCandidate(result, value);
        }

        AddRegionCandidate(result, "wor");
        return result;
    }

    private void AddRegionCandidates(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            AddRegionCandidate(target, value);
        }
    }

    private void AddRegionCandidates(List<string> target, string value)
    {
        AddRegionCandidate(target, value);
    }

    private void AddLanguageCandidates(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            AddLanguageCandidates(target, value);
        }
    }

    private void AddLanguageCandidates(List<string> target, string value)
    {
        var normalized = NormalizeKey(value).Replace("_", "-", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        switch (normalized)
        {
            case "ja" or "jp" or "japanese" or "ja-jp":
                AddRegionCandidate(target, "jp");
                break;
            case "pt-br" or "br" or "brazil" or "brazilian":
                AddRegionCandidate(target, "br");
                break;
            case "pt" or "portuguese":
                AddRegionCandidate(target, "br");
                AddRegionCandidate(target, "eu");
                break;
            case "en-us" or "us" or "usa":
                AddRegionCandidate(target, "us");
                break;
            case "en-gb" or "uk" or "gb":
                AddRegionCandidate(target, "uk");
                AddRegionCandidate(target, "eu");
                AddRegionCandidate(target, "us");
                break;
            case "en" or "english":
                AddRegionCandidate(target, "us");
                AddRegionCandidate(target, "uk");
                AddRegionCandidate(target, "eu");
                break;
            case "fr" or "fr-fr" or "french" or "france":
                AddRegionCandidate(target, "fr");
                AddRegionCandidate(target, "eu");
                break;
            case "de" or "de-de" or "german" or "germany":
                AddRegionCandidate(target, "de");
                AddRegionCandidate(target, "eu");
                break;
            case "es" or "es-es" or "sp" or "spanish" or "spain":
                AddRegionCandidate(target, "sp");
                AddRegionCandidate(target, "es");
                AddRegionCandidate(target, "eu");
                break;
            case "it" or "it-it" or "italian" or "italy":
                AddRegionCandidate(target, "it");
                AddRegionCandidate(target, "eu");
                break;
            case "nl" or "dutch" or "netherlands":
                AddRegionCandidate(target, "nl");
                AddRegionCandidate(target, "eu");
                break;
            default:
                if (normalized.Length == 2)
                {
                    AddRegionCandidate(target, "eu");
                }
                break;
        }
    }

    private void AddRegionCandidate(List<string> target, string value)
    {
        var normalized = _taxonomy.NormalizeScreenScraperRegionCode(value);
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized is "auto" or "cus" or "mor" or "ss" ||
            normalized.Contains('-', StringComparison.Ordinal))
        {
            return;
        }

        AddDistinct(target, normalized);
        if (normalized is "fr" or "de" or "it" or "nl" or "uk" or "sp" or "es")
        {
            AddDistinct(target, "eu");
        }
    }

    private static IEnumerable<string> BuildPlanTokens(MediaProjectionPlan plan)
    {
        foreach (var value in new[] { plan.GamePath, plan.DisplayName, plan.ProjectionBaseName, plan.GameSlug })
        {
            var fileName = Path.GetFileNameWithoutExtension(value ?? string.Empty);
            foreach (var token in SplitTokens(fileName))
            {
                yield return token;
            }

            foreach (var token in SplitTokens(value ?? string.Empty))
            {
                yield return token;
            }
        }
    }

    private static IEnumerable<string> SplitTokens(string value)
    {
        var normalized = (value ?? string.Empty)
            .Replace('[', ' ')
            .Replace(']', ' ')
            .Replace('(', ' ')
            .Replace(')', ' ')
            .Replace(',', ' ')
            .Replace(';', ' ')
            .Replace('_', ' ')
            .Replace('-', ' ');
        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length is >= 2 and <= 16);
    }

    private static void AddRomRegionToken(List<string> target, string value)
    {
        var normalized = NormalizeKey(value);
        var region = normalized switch
        {
            "u" or "usa" or "us" or "america" => "us",
            "e" or "eur" or "europe" or "eu" => "eu",
            "j" or "jpn" or "japan" or "jp" => "jp",
            "w" or "world" or "wor" => "wor",
            "fr" or "fra" or "france" => "fr",
            "de" or "ger" or "germany" => "de",
            "es" or "spa" or "spain" => "sp",
            "it" or "ita" or "italy" => "it",
            "uk" or "gb" => "uk",
            "br" or "brazil" => "br",
            "asia" or "asi" => "asi",
            "cn" or "china" => "cn",
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(region))
        {
            AddDistinct(target, region);
        }
    }

    private static void AddRomLanguageToken(List<string> target, string value)
    {
        var normalized = NormalizeKey(value).Replace("_", "-", StringComparison.Ordinal);
        var language = normalized switch
        {
            "fre" or "fra" or "fr" or "french" => "fr",
            "eng" or "en" or "english" => "en",
            "jpn" or "ja" or "jp" or "japanese" => "ja",
            "ger" or "de" or "german" => "de",
            "spa" or "es" or "sp" or "spanish" => "es",
            "ita" or "it" or "italian" => "it",
            "por" or "pt" or "portuguese" => "pt",
            "dut" or "nl" or "dutch" => "nl",
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(language))
        {
            AddDistinct(target, language);
        }
    }

    private static void AddDistinct(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            AddDistinct(target, value);
        }
    }

    private static void AddDistinct(List<string> target, string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalized) &&
            !target.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(normalized);
        }
    }

    private static string NormalizeKey(string value)
    {
        return (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace(' ', '_');
    }
}
