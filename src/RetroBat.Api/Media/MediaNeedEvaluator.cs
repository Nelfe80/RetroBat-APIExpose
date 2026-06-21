using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Media;

public class MediaNeedEvaluator
{
    private readonly MediaSystemRules _systemRules;
    private readonly EmulationStationSettingsService _settingsService;

    public MediaNeedEvaluator(MediaSystemRules systemRules, EmulationStationSettingsService settingsService)
    {
        _systemRules = systemRules;
        _settingsService = settingsService;
    }

    public MediaProjectionPlan BuildPlan(MediaPrefetchRequest request, string normalizedSystemId, string gameSlug, string frontendSystemId)
    {
        var scrapingSettings = _settingsService.GetScrapingSettings();
        var plan = new MediaProjectionPlan
        {
            SystemId = normalizedSystemId,
            FrontendSystemId = frontendSystemId,
            GameSlug = gameSlug,
            DisplayName = request.GameName,
            GamePath = request.GamePath,
            ProjectionBaseName = BuildProjectionBaseName(request),
            IsArcadeLike = _systemRules.IsArcadeLike(normalizedSystemId),
            IsFolderBasedSystem = _systemRules.IsFolderBasedSystem(normalizedSystemId),
            SkipCrcComputation = _systemRules.SkipCrcComputation(normalizedSystemId, request.GamePath),
            IsFilteredArcadeBiosCandidate = _systemRules.IsFilteredArcadeBiosCandidate(normalizedSystemId, request.GamePath),
            NeedsDescriptionScrape = string.IsNullOrWhiteSpace(request.Details?.Desc),
            GamePathExists = GamePathExists(request.GamePath),
            GamelistMd5 = NormalizeMd5(request.Details?.Md5),
            GamelistCrc32 = NormalizeCrc32(ResolveExtra(request.Details, "crc32")),
            GamelistPath = Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId, "gamelist.xml"),
            PreferredImageSource = scrapingSettings.ImageSource,
            PreferredLogoSource = scrapingSettings.LogoSource,
            PreferredThumbnailSource = scrapingSettings.ThumbSource
        };

        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Image, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Thumbnail, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Logo, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Wheel, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.WheelCarbon, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.WheelSteel, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Marquee, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.ScreenMarquee, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.ScreenMarqueeSmall, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.SteamGrid, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.MixRbv1, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.MixRbv2, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.BoxFront, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.BoxSide, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.BoxTexture, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Box3d, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Cartridge, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Label, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Fanart, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Flyer, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Figurine, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Bezel, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.BoxBack, "images");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Map, "manuals");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Manual, "manuals");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Magazine, "manuals");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.Video, "videos");
        AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.VideoNormalized, "videos");
        if (scrapingSettings.IsHyperBatThemeActive && IsInstalledThemeSet(scrapingSettings.ThemeSet))
        {
            AddNeed(plan, request, normalizedSystemId, frontendSystemId, gameSlug, MediaKinds.ThemeHb, "themes");
        }

        return plan;
    }

    private void AddNeed(
        MediaProjectionPlan plan,
        MediaPrefetchRequest request,
        string systemId,
        string frontendSystemId,
        string gameSlug,
        string kind,
        string folderName)
    {
        plan.Needs.Add(BuildNeed(request, systemId, frontendSystemId, gameSlug, kind, folderName));
    }

    private MediaNeed BuildNeed(MediaPrefetchRequest request, string systemId, string frontendSystemId, string gameSlug, string kind, string folderName)
    {
        var projectionBaseName = BuildProjectionBaseName(request);
        var existing = kind switch
        {
            MediaKinds.Image => request.Details?.Image ?? string.Empty,
            MediaKinds.Thumbnail => request.Details?.Thumbnail ?? string.Empty,
            MediaKinds.Logo => request.Details?.Extras.GetValueOrDefault("logo")
                ?? request.Details?.Extras.GetValueOrDefault("wheel")
                ?? string.Empty,
            MediaKinds.Wheel => request.Details?.Extras.GetValueOrDefault("wheel")
                ?? request.Details?.Extras.GetValueOrDefault("logo")
                ?? string.Empty,
            MediaKinds.WheelCarbon => request.Details?.Extras.GetValueOrDefault("wheel-carbon") ?? string.Empty,
            MediaKinds.WheelSteel => request.Details?.Extras.GetValueOrDefault("wheel-steel") ?? string.Empty,
            // ES uses <marquee> as the visible logo slot, so it may point to wheel/logo.
            // Do not reuse it as proof of a real ScreenScraper marquee asset.
            MediaKinds.Marquee => string.Empty,
            MediaKinds.ScreenMarquee => request.Details?.Extras.GetValueOrDefault("screenmarquee") ?? string.Empty,
            MediaKinds.ScreenMarqueeSmall => request.Details?.Extras.GetValueOrDefault("screenmarqueesmall") ?? string.Empty,
            MediaKinds.SteamGrid => request.Details?.Extras.GetValueOrDefault("steamgrid") ?? string.Empty,
            MediaKinds.MixRbv1 => request.Details?.Extras.GetValueOrDefault("mixrbv1") ?? string.Empty,
            MediaKinds.MixRbv2 => request.Details?.Extras.GetValueOrDefault("mixrbv2")
                ?? request.Details?.Extras.GetValueOrDefault("mix")
                ?? string.Empty,
            MediaKinds.BoxFront => request.Details?.Extras.GetValueOrDefault("box-2D") ?? string.Empty,
            MediaKinds.BoxSide => request.Details?.Extras.GetValueOrDefault("box-2D-side") ?? string.Empty,
            MediaKinds.BoxTexture => request.Details?.Extras.GetValueOrDefault("box-texture") ?? string.Empty,
            MediaKinds.Box3d => request.Details?.Extras.GetValueOrDefault("box-3D") ?? string.Empty,
            MediaKinds.Cartridge => request.Details?.Extras.GetValueOrDefault("cartridge")
                ?? request.Details?.Extras.GetValueOrDefault("support-2D")
                ?? string.Empty,
            MediaKinds.Label => request.Details?.Extras.GetValueOrDefault("label")
                ?? request.Details?.Extras.GetValueOrDefault("support-texture")
                ?? string.Empty,
            MediaKinds.Fanart => request.Details?.Fanart ?? string.Empty,
            MediaKinds.Flyer => request.Details?.Extras.GetValueOrDefault("flyer") ?? string.Empty,
            MediaKinds.Figurine => request.Details?.Extras.GetValueOrDefault("figurine") ?? string.Empty,
            MediaKinds.Bezel => request.Details?.Bezel ?? string.Empty,
            MediaKinds.BoxBack => request.Details?.Boxback ?? string.Empty,
            MediaKinds.Map => request.Details?.Map ?? request.Details?.Extras.GetValueOrDefault("map") ?? string.Empty,
            MediaKinds.Manual => request.Details?.Manual ?? string.Empty,
            MediaKinds.Magazine => request.Details?.Extras.GetValueOrDefault("magazine") ?? string.Empty,
            MediaKinds.Video => request.Details?.Video ?? string.Empty,
            MediaKinds.VideoNormalized => request.Details?.Extras.GetValueOrDefault("video-normalized") ?? string.Empty,
            MediaKinds.ThemeHb => request.Details?.Extras.GetValueOrDefault("themehb") ?? string.Empty,
            _ => string.Empty
        };

        if (string.Equals(kind, MediaKinds.Image, StringComparison.OrdinalIgnoreCase) &&
            IsImagePlaceholder(existing, projectionBaseName))
        {
            existing = string.Empty;
        }
        else if (string.Equals(kind, MediaKinds.ThemeHb, StringComparison.OrdinalIgnoreCase))
        {
            existing = ResolveCanonicalThemeHbArchivePath(systemId, gameSlug)
                ?? (IsExistingThemeHbArchiveUsable(frontendSystemId, existing) ? existing : string.Empty);
        }
        else if (!string.IsNullOrWhiteSpace(existing) && !ExistingMediaPathExists(frontendSystemId, existing))
        {
            existing = string.Empty;
        }

        var extension = kind switch
        {
            MediaKinds.Manual => ".pdf",
            MediaKinds.Video or MediaKinds.VideoNormalized => ".mp4",
            MediaKinds.ThemeHb => ".zip",
            _ => ".png"
        };

        var fileName = BuildProjectionFileName(projectionBaseName, kind, extension);

        return new MediaNeed
        {
            Kind = kind,
            IsMissing = string.IsNullOrWhiteSpace(existing),
            InitialExistingPath = existing,
            ExistingPath = existing,
            TargetRelativePath = Path.Combine(folderName, fileName)
        };
    }

    private string? ResolveCanonicalThemeHbArchivePath(string systemId, string gameSlug)
    {
        var storageSystemId = _systemRules.IsArcadeLike(systemId) ? "arcade" : systemId;
        if (string.IsNullOrWhiteSpace(storageSystemId) || string.IsNullOrWhiteSpace(gameSlug))
        {
            return null;
        }

        var candidate = Path.Combine(
            RetroBatPaths.MediaSystemsRoot,
            storageSystemId,
            "games",
            gameSlug,
            "themes",
            "themehb.zip");
        return File.Exists(candidate) && IsZipArchive(candidate)
            ? candidate
            : null;
    }

    private static bool IsInstalledThemeSet(string themeSet)
    {
        if (string.IsNullOrWhiteSpace(themeSet) ||
            Path.IsPathRooted(themeSet) ||
            themeSet.Contains(Path.DirectorySeparatorChar) ||
            themeSet.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        return Directory.Exists(Path.Combine(RetroBatPaths.EmulationStationThemesRoot, themeSet.Trim()));
    }

    private static bool IsImagePlaceholder(string? path, string projectionBaseName)
    {
        var normalized = (path ?? string.Empty).Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        var expectedSuffix = $"{projectionBaseName}_default.png";
        return normalized.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/scraping_in_progress.png", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("scraping_in_progress.png", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/no_media_found.png", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("no_media_found.png", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExistingThemeHbArchiveUsable(string frontendSystemId, string? path)
    {
        var resolved = ResolveExistingMediaPath(frontendSystemId, path);
        return !string.IsNullOrWhiteSpace(resolved) &&
            File.Exists(resolved) &&
            IsZipArchive(resolved);
    }

    private static bool ExistingMediaPathExists(string frontendSystemId, string? path)
    {
        var resolved = ResolveExistingMediaPath(frontendSystemId, path);
        return !string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved);
    }

    private static string? ResolveExistingMediaPath(string frontendSystemId, string? path)
    {
        var normalized = (path ?? string.Empty).Replace('/', Path.DirectorySeparatorChar).Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.StartsWith(Path.DirectorySeparatorChar + "systems" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Path.IsPathRooted(normalized))
        {
            return Path.GetFullPath(normalized);
        }

        if (normalized.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return Path.GetFullPath(Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId, normalized));
    }

    private static bool IsZipArchive(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[4];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Read(header) < header.Length)
            {
                return false;
            }

            return IsZipHeader(header, 0x03, 0x04) ||
                IsZipHeader(header, 0x05, 0x06) ||
                IsZipHeader(header, 0x07, 0x08);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsZipHeader(ReadOnlySpan<byte> header, byte third, byte fourth)
    {
        return header.Length >= 4 &&
            header[0] == 0x50 &&
            header[1] == 0x4B &&
            header[2] == third &&
            header[3] == fourth;
    }

    private static string BuildProjectionBaseName(MediaPrefetchRequest request)
    {
        var raw = Path.GetFileNameWithoutExtension(request.GamePath);
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = request.GameName;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = request.GameId;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(raw.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "game" : cleaned;
    }

    private static bool GamePathExists(string? path)
    {
        return !string.IsNullOrWhiteSpace(path)
            && (File.Exists(path) || Directory.Exists(path));
    }

    private static string NormalizeMd5(string? value)
    {
        var md5 = (value ?? string.Empty).Trim();
        if (md5.Length != 32)
        {
            return string.Empty;
        }

        return md5.All(IsHex)
            ? md5.ToLowerInvariant()
            : string.Empty;
    }

    private static string NormalizeCrc32(string? value)
    {
        var crc32 = (value ?? string.Empty).Trim();
        if (crc32.Length != 8)
        {
            return string.Empty;
        }

        return crc32.All(IsHex)
            ? crc32.ToUpperInvariant()
            : string.Empty;
    }

    private static string ResolveExtra(GameDetails? details, string key)
    {
        if (details?.Extras == null)
        {
            return string.Empty;
        }

        return details.Extras.TryGetValue(key, out var value) ? value : string.Empty;
    }

    private static bool IsHex(char ch)
    {
        return ch is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';
    }

    private static string BuildProjectionFileName(string baseName, string kind, string extension)
    {
        var suffix = kind switch
        {
            MediaKinds.Image => "-screentitle",
            MediaKinds.Thumbnail => "-screenshot",
            MediaKinds.Logo => "-logo",
            MediaKinds.Wheel => "-wheel",
            MediaKinds.WheelCarbon => "-wheelcarbon",
            MediaKinds.WheelSteel => "-wheelsteel",
            MediaKinds.Marquee => "-marquee",
            MediaKinds.ScreenMarquee => "-screenmarquee",
            MediaKinds.ScreenMarqueeSmall => "-screenmarqueesmall",
            MediaKinds.SteamGrid => "-steamgrid",
            MediaKinds.MixRbv1 => "-mixrbv1",
            MediaKinds.MixRbv2 => "-mixrbv2",
            MediaKinds.BoxFront => "-box2d",
            MediaKinds.BoxSide => "-boxside",
            MediaKinds.BoxTexture => "-boxtexture",
            MediaKinds.Box3d => "-box3d",
            MediaKinds.Cartridge => "-cartridge",
            MediaKinds.Label => "-label",
            MediaKinds.Fanart => "-fanart",
            MediaKinds.Flyer => "-flyer",
            MediaKinds.Figurine => "-figurine",
            MediaKinds.Bezel => "-bezel",
            MediaKinds.BoxBack => "-boxback",
            MediaKinds.Map => "-map",
            MediaKinds.Manual => "-manual",
            MediaKinds.Magazine => "-magazine",
            MediaKinds.Video => "-video",
            MediaKinds.VideoNormalized => "-video-normalized",
            MediaKinds.ThemeHb => "-themehb",
            _ => "-" + kind
        };

        return baseName + suffix + extension;
    }
}
