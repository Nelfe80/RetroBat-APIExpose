namespace RetroBat.Domain.Models;

public static class MediaKinds
{
    public const string LegacyScreenMarquee = "screenmarquee";
    public const string LegacyBoxBack = "boxback";
    public const string Image = "image";
    public const string Thumbnail = "thumbnail";
    public const string Logo = "logo";
    public const string Wheel = "wheel";
    public const string WheelCarbon = "wheel-carbon";
    public const string WheelSteel = "wheel-steel";
    public const string Marquee = "marquee";
    public const string ScreenMarquee = "screen-marquee";
    public const string ScreenMarqueeSmall = "screen-marquee-small";
    public const string SteamGrid = "steamgrid";
    public const string MixRbv1 = "mixrbv1";
    public const string MixRbv2 = "mixrbv2";
    public const string BoxFront = "box-front";
    public const string BoxSide = "box-side";
    public const string BoxTexture = "box-texture";
    public const string Box3d = "box-3d";
    public const string Cartridge = "cartridge";
    public const string Label = "label";
    public const string Fanart = "fanart";
    public const string Flyer = "flyer";
    public const string Figurine = "figurine";
    public const string Bezel = "bezel";
    public const string BoxBack = "box-back";
    public const string Map = "map";
    public const string Manual = "manual";
    public const string Magazine = "magazine";
    public const string Video = "video";
    public const string VideoNormalized = "video-normalized";
    public const string ThemeHb = "themehb";

    public static string Normalize(string? kind)
    {
        var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "sstitle" => Image,
            "screentitle" => Image,
            "screen-title" => Image,
            "title" => Image,
            "ss" => Thumbnail,
            "screenshot" => Thumbnail,
            "thumb" => Thumbnail,
            LegacyScreenMarquee => ScreenMarquee,
            "screen_marquee" => ScreenMarquee,
            "screenmarqueesmall" => ScreenMarqueeSmall,
            "screen-marquee-small" => ScreenMarqueeSmall,
            "screen_marquee_small" => ScreenMarqueeSmall,
            "mix" => MixRbv2,
            "mix-rbv1" => MixRbv1,
            "mix_rbv1" => MixRbv1,
            "mix-rbv2" => MixRbv2,
            "mix_rbv2" => MixRbv2,
            "cart" => Cartridge,
            "support-2d" => Cartridge,
            "support2d" => Cartridge,
            "support-texture" => Label,
            "supporttexture" => Label,
            "video_normalized" => VideoNormalized,
            "theme-hb" => ThemeHb,
            "theme_hb" => ThemeHb,
            LegacyBoxBack => BoxBack,
            "box_back" => BoxBack,
            _ => normalized
        };
    }
}

public class MediaPrefetchRequest
{
    public string SystemId { get; set; } = string.Empty;
    public string GameId { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public GameDetails? Details { get; set; }
}

public class MediaNeed
{
    public string Kind { get; set; } = string.Empty;
    public bool IsMissing { get; set; }
    public string InitialExistingPath { get; set; } = string.Empty;
    public string ExistingPath { get; set; } = string.Empty;
    public string TargetRelativePath { get; set; } = string.Empty;
    public string ImportedPath { get; set; } = string.Empty;
    public string ProjectedPath { get; set; } = string.Empty;
    public bool WasImported { get; set; }
    public bool WasProjected { get; set; }
    public bool WasContentChanged { get; set; }
}

public class MediaProjectionPlan
{
    public string SystemId { get; set; } = string.Empty;
    public string FrontendSystemId { get; set; } = string.Empty;
    public string GameSlug { get; set; } = string.Empty;
    public string TextSourceGameSlug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public string ProjectionBaseName { get; set; } = string.Empty;
    public string PreferredImageSource { get; set; } = string.Empty;
    public string PreferredLogoSource { get; set; } = string.Empty;
    public string PreferredThumbnailSource { get; set; } = string.Empty;
    public bool IsArcadeLike { get; set; }
    public bool IsFolderBasedSystem { get; set; }
    public bool SkipCrcComputation { get; set; }
    public bool IsFilteredArcadeBiosCandidate { get; set; }
    public bool NeedsDescriptionScrape { get; set; }
    public bool IgnoreRemoteScrapeCooldown { get; set; }
    public bool GamePathExists { get; set; }
    public string GamelistMd5 { get; set; } = string.Empty;
    public string GamelistCrc32 { get; set; } = string.Empty;
    public string GamelistPath { get; set; } = string.Empty;
    public string EsGameId { get; set; } = string.Empty;
    public string ScreenScraperGameId { get; set; } = string.Empty;
    public List<string> RomRegions { get; set; } = new();
    public List<string> RomLanguages { get; set; } = new();
    public bool SuppressImmediateGamelistUpdates { get; set; }
    public List<MediaNeed> Needs { get; set; } = new();
}

public class MediaPrefetchResult
{
    public string SystemId { get; set; } = string.Empty;
    public string GameSlug { get; set; } = string.Empty;
    public bool QueuedRemoteScrape { get; set; }
    public bool IsArcadeLike { get; set; }
    public bool IsFolderBasedSystem { get; set; }
    public bool SkipCrcComputation { get; set; }
    public bool IsFilteredArcadeBiosCandidate { get; set; }
    public bool GamePathExists { get; set; }
    public string GamelistMd5 { get; set; } = string.Empty;
    public string GamelistCrc32 { get; set; } = string.Empty;
    public List<MediaNeed> Needs { get; set; } = new();
}
