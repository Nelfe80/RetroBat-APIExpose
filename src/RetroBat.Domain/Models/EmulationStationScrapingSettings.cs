namespace RetroBat.Domain.Models;

public class EmulationStationScrapingSettings
{
    public bool PublicWebAccessEnabled { get; set; }
    public bool ScrapeBezel { get; set; }
    public bool ScrapeBoxBack { get; set; }
    public bool ScrapeFanart { get; set; }
    public bool ScrapeManual { get; set; }
    public bool ScrapeMap { get; set; }
    public bool ScrapeVideos { get; set; }
    public bool ShowManualIcon { get; set; }
    public string Language { get; set; } = "en_US";
    public string ImageSource { get; set; } = "ss";
    public string LogoSource { get; set; } = "logo";
    public string ThumbSource { get; set; } = "ss";
    public string WheelStyle { get; set; } = "carbon";
    public string MediaRegionMode { get; set; } = "match_rom_region";
    public string LogoRegionMode { get; set; } = "user_language";
    public string UserRegion { get; set; } = "auto";
    public string ContentRegionProfile { get; set; } = "usa";
    public string ContentLanguageProfile { get; set; } = "english";
    public string ScreenScraperUser { get; set; } = string.Empty;
    public string ScreenScraperPassword { get; set; } = string.Empty;
    public string ThemeSet { get; set; } = string.Empty;

    public bool IsHyperBatThemeActive =>
        ThemeSet.Trim().Contains("hyperbat", StringComparison.OrdinalIgnoreCase);

    public bool IsImageScrapingEnabled =>
        !string.IsNullOrWhiteSpace(ImageSource)
        && !string.Equals(ImageSource, "none", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(ImageSource, "disabled", StringComparison.OrdinalIgnoreCase);

    public bool IsKindEnabled(string kind)
    {
        return MediaKinds.Normalize(kind) switch
        {
            MediaKinds.Image => IsImageScrapingEnabled,
            MediaKinds.Thumbnail => IsImageScrapingEnabled,
            MediaKinds.Logo => IsImageScrapingEnabled,
            MediaKinds.Wheel => IsImageScrapingEnabled,
            MediaKinds.WheelCarbon => IsImageScrapingEnabled,
            MediaKinds.WheelSteel => IsImageScrapingEnabled,
            MediaKinds.Marquee => IsImageScrapingEnabled,
            MediaKinds.ScreenMarquee => IsImageScrapingEnabled,
            MediaKinds.ScreenMarqueeSmall => IsImageScrapingEnabled,
            MediaKinds.SteamGrid => IsImageScrapingEnabled,
            MediaKinds.MixRbv1 => IsImageScrapingEnabled,
            MediaKinds.MixRbv2 => IsImageScrapingEnabled,
            MediaKinds.BoxFront => IsImageScrapingEnabled,
            MediaKinds.BoxSide => IsImageScrapingEnabled,
            MediaKinds.BoxTexture => IsImageScrapingEnabled,
            MediaKinds.Box3d => IsImageScrapingEnabled,
            MediaKinds.Cartridge => IsImageScrapingEnabled,
            MediaKinds.Label => IsImageScrapingEnabled,
            MediaKinds.Fanart => ScrapeFanart,
            MediaKinds.Flyer => IsImageScrapingEnabled,
            MediaKinds.Figurine => IsImageScrapingEnabled,
            MediaKinds.Bezel => ScrapeBezel,
            MediaKinds.BoxBack => ScrapeBoxBack,
            MediaKinds.Map => ScrapeMap,
            MediaKinds.Manual => ScrapeManual,
            MediaKinds.Video => ScrapeVideos,
            MediaKinds.VideoNormalized => ScrapeVideos,
            MediaKinds.ThemeHb => IsHyperBatThemeActive,
            _ => false
        };
    }
}
