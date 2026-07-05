namespace RetroBat.Domain.Services;

public static class ApiExposeProfileResolver
{
    public const string DefaultLanguageProfile = "english";
    public const string DefaultRegionProfile = "usa";

    public static ApiExposeResolvedProfiles Resolve(string? esLanguage, string? languageProfile, string? regionProfile)
    {
        var language = ResolveLanguageProfile(esLanguage, languageProfile);
        var region = ResolveRegionProfile(esLanguage, regionProfile, language);
        return new ApiExposeResolvedProfiles(language, region);
    }

    public static string ResolveLanguageCode(string? esLanguage, string? languageProfile)
    {
        var normalizedEsLanguage = NormalizeLocale(esLanguage);
        if (!string.IsNullOrWhiteSpace(normalizedEsLanguage))
        {
            return normalizedEsLanguage;
        }

        return ResolveLanguageFromProfile(languageProfile) ?? "en_US";
    }

    private static string ResolveLanguageProfile(string? esLanguage, string? languageProfile)
    {
        if (!IsAuto(languageProfile))
        {
            return languageProfile!.Trim();
        }

        return ResolveLanguageProfileFromLocale(esLanguage) ?? DefaultLanguageProfile;
    }

    private static string ResolveRegionProfile(string? esLanguage, string? regionProfile, string languageProfile)
    {
        if (!IsAuto(regionProfile))
        {
            return regionProfile!.Trim();
        }

        return ResolveRegionProfileFromLocale(esLanguage) ??
            ResolveRegionProfileFromLanguageProfile(languageProfile) ??
            DefaultRegionProfile;
    }

    private static string? ResolveLanguageProfileFromLocale(string? esLanguage)
    {
        return NormalizeLanguagePart(esLanguage) switch
        {
            "fr" => "french",
            "en" => "english",
            "ja" => "japanese",
            "de" => "german",
            "es" => "spanish",
            "it" => "italian",
            "pt" => "portuguese",
            "ko" => "korean",
            "zh" => "chinese",
            "ru" => "russian",
            "nl" => "dutch",
            "sv" => "swedish",
            "pl" => "polish",
            "da" => "danish",
            "fi" => "finnish",
            _ => null
        };
    }

    private static string? ResolveRegionProfileFromLocale(string? esLanguage)
    {
        var normalized = NormalizeLocale(esLanguage).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var parts = normalized.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var language = parts.Length > 0 ? parts[0] : string.Empty;
        var country = parts.Length > 1 ? parts[1] : string.Empty;

        return country switch
        {
            "fr" => "france",
            "us" => "usa",
            "gb" or "uk" => "united_kingdom",
            "jp" => "japan",
            "de" => "germany",
            "es" => "spain",
            "it" => "italy",
            "br" => "brazil",
            "kr" => "korea",
            "cn" => "china",
            "tw" => "taiwan",
            "nl" => "netherlands",
            "se" => "sweden",
            "ru" => "russia",
            "au" => "australia",
            "ca" => "canada",
            _ => language switch
            {
                "fr" => "france",
                "en" => "usa",
                "ja" => "japan",
                "de" => "germany",
                "es" => "spain",
                "it" => "italy",
                "pt" => "brazil",
                "ko" => "korea",
                "zh" => "china",
                "nl" => "netherlands",
                "sv" => "sweden",
                "ru" => "russia",
                _ => null
            }
        };
    }

    private static string? ResolveRegionProfileFromLanguageProfile(string? languageProfile)
    {
        return NormalizeKey(languageProfile) switch
        {
            "french" or "fr" => "france",
            "english" or "en" => "usa",
            "japanese" or "ja" or "jp" => "japan",
            "german" or "de" => "germany",
            "spanish" or "es" => "spain",
            "italian" or "it" => "italy",
            "portuguese" or "pt" => "brazil",
            "korean" or "ko" => "korea",
            "chinese" or "zh" => "china",
            "dutch" or "nl" => "netherlands",
            "swedish" or "sv" => "sweden",
            "russian" or "ru" => "russia",
            "multilingual" or "multi" => "world",
            _ => null
        };
    }

    private static string? ResolveLanguageFromProfile(string? profile)
    {
        return NormalizeKey(profile).Replace('_', '-') switch
        {
            "english" or "en" or "en-us" or "usa" or "us" => "en_US",
            "french" or "fr" or "fr-fr" or "france" => "fr_FR",
            "german" or "de" or "de-de" or "germany" => "de_DE",
            "spanish" or "es" or "es-es" or "spain" => "es_ES",
            "italian" or "it" or "it-it" or "italy" => "it_IT",
            "japanese" or "ja" or "jp" or "ja-jp" or "japan" => "ja_JP",
            "portuguese" or "pt" or "pt-br" or "brazil" or "br" => "pt_BR",
            "korean" or "ko" or "kr" => "ko_KR",
            "chinese" or "zh" or "cn" => "zh_CN",
            "russian" or "ru" => "ru_RU",
            "dutch" or "nl" => "nl_NL",
            var normalized when normalized.Length == 2 => normalized,
            _ => null
        };
    }

    private static bool IsAuto(string? value)
    {
        var normalized = NormalizeKey(value);
        return string.IsNullOrWhiteSpace(normalized) || normalized is "auto" or "automatic";
    }

    private static string NormalizeLocale(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().Replace('-', '_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var parts = normalized.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        return parts.Length == 1
            ? parts[0].ToLowerInvariant()
            : $"{parts[0].ToLowerInvariant()}_{parts[1].ToUpperInvariant()}";
    }

    private static string NormalizeLanguagePart(string? value)
    {
        return NormalizeLocale(value).Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
    }

    private static string NormalizeKey(string? value)
    {
        return (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace('-', '_')
            .Replace(' ', '_');
    }
}

public sealed record ApiExposeResolvedProfiles(string LanguageProfile, string RegionProfile);
