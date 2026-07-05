using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;

namespace RetroBat.Domain.Services;

public class EmulationStationSettingsService
{
    private readonly object _sync = new();
    private DateTime _lastWriteTimeUtc = DateTime.MinValue;
    private EmulationStationScrapingSettings _cachedScrapingSettings = new();
    private readonly IEsSettingsStore _settingsStore;

    public EmulationStationSettingsService()
        : this(new EsSettingsStore())
    {
    }

    public EmulationStationSettingsService(IEsSettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public EmulationStationScrapingSettings GetScrapingSettings()
    {
        EnsureLoaded();
        return _cachedScrapingSettings;
    }

    public IReadOnlyDictionary<string, string> GetAllSettings()
    {
        return _settingsStore.ReadAllSettings();
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _cachedScrapingSettings = new EmulationStationScrapingSettings();
            _lastWriteTimeUtc = DateTime.MinValue;
            _settingsStore.Invalidate();
        }
    }

    private void EnsureLoaded()
    {
        var snapshot = _settingsStore.ReadSnapshot();

        lock (_sync)
        {
            if (snapshot.LastWriteTimeUtc == _lastWriteTimeUtc)
            {
                return;
            }

            _cachedScrapingSettings = BuildScrapingSettings(snapshot.Values, _cachedScrapingSettings);
            _lastWriteTimeUtc = snapshot.LastWriteTimeUtc;
        }
    }

    private static EmulationStationScrapingSettings BuildScrapingSettings(
        IReadOnlyDictionary<string, string> settings,
        EmulationStationScrapingSettings previous)
    {
        var hasApiExposeMediaAllocation = HasAnyValue(
            settings,
            "global.apiexpose.media_allocation.image_source",
            "global.apiexpose.media_allocation.logo_source",
            "global.apiexpose.media_allocation.thumb_source",
            "global.apiexpose.media_allocation.wheel_style",
            "global.apiexpose.media_allocation.region_mode",
            "global.apiexpose.media_allocation.logo_region_mode",
            "global.apiexpose.media_allocation.user_region");
        var syncGamelistsWithSystemLanguage = ReadBool(settings, "global.apiexpose.api.sync_gamelists_with_system_language");
        var rawEsLanguage = FirstValue(settings, "Language");
        var esLanguage = rawEsLanguage;
        if (syncGamelistsWithSystemLanguage && string.IsNullOrWhiteSpace(rawEsLanguage))
        {
            esLanguage = "en_US";
        }

        var rawContentLanguageProfile = syncGamelistsWithSystemLanguage && !string.IsNullOrWhiteSpace(esLanguage)
            ? "auto"
            : FirstValue(
            settings,
            "global.apiexpose.api.language_profile",
            "global.apiexpose.romset.language_profile") ?? previous.ContentLanguageProfile;
        var rawContentRegionProfile = syncGamelistsWithSystemLanguage && !string.IsNullOrWhiteSpace(esLanguage)
            ? "auto"
            : FirstValue(
            settings,
            "global.apiexpose.api.region_profile",
            "global.apiexpose.romset.region_profile") ?? previous.ContentRegionProfile;
        var effectiveProfiles = ApiExposeProfileResolver.Resolve(
            esLanguage,
            rawContentLanguageProfile,
            rawContentRegionProfile);

        return new EmulationStationScrapingSettings
        {
            PublicWebAccessEnabled = ReadBool(settings, "PublicWebAccess"),
            ScrapeBezel = ReadBool(settings, "ScrapeBezel"),
            ScrapeBoxBack = ReadBool(settings, "ScrapeBoxBack"),
            ScrapeFanart = ReadBool(settings, "ScrapeFanart"),
            ScrapeManual = ReadBool(settings, "ScrapeManual"),
            ScrapeMap = ReadBool(settings, "ScrapeMap"),
            ScrapeVideos = ReadBool(settings, "ScrapeVideos"),
            ShowManualIcon = ReadBool(settings, "ShowManualIcon"),
            Language = ApiExposeProfileResolver.ResolveLanguageCode(esLanguage, effectiveProfiles.LanguageProfile),
            ImageSource = ReadMediaAllocationValue(settings, previous.ImageSource, hasApiExposeMediaAllocation, "ss", "global.apiexpose.media_allocation.image_source", "ScrapperImageSrc", "ScraperImageSrc", "ImageSource"),
            LogoSource = ReadMediaAllocationValue(settings, previous.LogoSource, hasApiExposeMediaAllocation, "logo", "global.apiexpose.media_allocation.logo_source", "ScrapperLogoSrc", "ScraperLogoSrc", "LogoSource"),
            ThumbSource = NormalizeLegacyThumbSource(ReadMediaAllocationValue(settings, previous.ThumbSource, hasApiExposeMediaAllocation, "ss", "global.apiexpose.media_allocation.thumb_source", "ScrapperThumbSrc", "ScraperThumbSrc", "ThumbSource")) ?? "ss",
            WheelStyle = ReadMediaAllocationValue(settings, previous.WheelStyle, hasApiExposeMediaAllocation, "carbon", "global.apiexpose.media_allocation.wheel_style", "global.apiexpose.scraping.wheel_style"),
            MediaRegionMode = ReadMediaAllocationValue(settings, previous.MediaRegionMode, hasApiExposeMediaAllocation, "match_rom_region", "global.apiexpose.media_allocation.region_mode"),
            LogoRegionMode = ReadMediaAllocationValue(settings, previous.LogoRegionMode, hasApiExposeMediaAllocation, "user_language", "global.apiexpose.media_allocation.logo_region_mode"),
            UserRegion = ReadMediaAllocationValue(settings, previous.UserRegion, hasApiExposeMediaAllocation, "auto", "global.apiexpose.media_allocation.user_region"),
            ContentRegionProfile = effectiveProfiles.RegionProfile,
            ContentLanguageProfile = effectiveProfiles.LanguageProfile,
            ScreenScraperUser = FirstValue(settings, "ScreenScraperUser", "ScraperUser", "scraper_user") ?? string.Empty,
            ScreenScraperPassword = FirstValue(settings, "ScreenScraperPass", "ScreenScraperPassword", "ScraperPassword", "scraper_password") ?? string.Empty,
            ThemeSet = FirstValue(settings, "ThemeSet") ?? string.Empty
        };
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> settings, string key)
    {
        if (!settings.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("1", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstValue(IReadOnlyDictionary<string, string> settings, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool HasAnyValue(IReadOnlyDictionary<string, string> settings, params string[] keys)
    {
        return keys.Any(key => settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value));
    }

    private static string ReadMediaAllocationValue(
        IReadOnlyDictionary<string, string> settings,
        string previousValue,
        bool hasApiExposeMediaAllocation,
        string defaultValue,
        string apiExposeKey,
        params string[] legacyKeys)
    {
        var apiExposeValue = FirstValue(settings, apiExposeKey);
        if (!string.IsNullOrWhiteSpace(apiExposeValue))
        {
            return apiExposeValue;
        }

        if (hasApiExposeMediaAllocation && !string.IsNullOrWhiteSpace(previousValue))
        {
            return previousValue;
        }

        return FirstValue(settings, legacyKeys) ?? defaultValue;
    }

    private static string? NormalizeLegacyThumbSource(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Equals("thumbnail", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("thumb", StringComparison.OrdinalIgnoreCase)
            ? "ss"
            : string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
