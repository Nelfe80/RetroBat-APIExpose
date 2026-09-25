using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Media;

public class MediaNeedEvaluator
{
    private readonly MediaSystemRules _systemRules;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly RomMetadataResolver _romMetadataResolver;
    private readonly GameMediaCatalogService _catalog;
    private readonly MediaScrapePlanner _scrapePlanner;
    private readonly IOptionsMonitor<ApiExposeOptions> _options;

    private static readonly char[] IdentitySeparators = [',', '/', ';', '+', ' '];

    public MediaNeedEvaluator(
        MediaSystemRules systemRules,
        EmulationStationSettingsService settingsService,
        RomMetadataResolver romMetadataResolver,
        GameMediaCatalogService catalog,
        MediaScrapePlanner scrapePlanner,
        IOptionsMonitor<ApiExposeOptions> options)
    {
        _systemRules = systemRules;
        _settingsService = settingsService;
        _romMetadataResolver = romMetadataResolver;
        _catalog = catalog;
        _scrapePlanner = scrapePlanner;
        _options = options;
    }

    public MediaProjectionPlan BuildPlan(MediaPrefetchRequest request, string normalizedSystemId, string gameSlug, string frontendSystemId)
    {
        var scrapingSettings = _settingsService.GetScrapingSettings();

        // Single source of truth for the ROM's lang/region identity: the name tags +
        // compact referential first, the gamelist metadata only as a fallback. Never
        // synthesise a region when it is genuinely unknown (leaves the list empty so
        // downstream keeps the existing value instead of stamping "wr").
        var identity = _romMetadataResolver.Resolve(normalizedSystemId, request.GamePath, request.GameName);
        var romRegions = identity.Regions.Count > 0
            ? identity.Regions.ToList()
            : SplitIdentity(request.Details?.Region);
        var romLanguages = identity.Languages.Count > 0
            ? identity.Languages.ToList()
            : SplitIdentity(request.Details?.Lang);

        var plan = new MediaProjectionPlan
        {
            RomRegions = romRegions,
            RomLanguages = romLanguages,
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

        if (_options.CurrentValue.MediaDiscovery?.ScrapePlannerEnabled == true)
        {
            ApplyResolverDrivenNeeds(plan, normalizedSystemId, gameSlug, request.GamePath);
        }

        return plan;
    }

    /// <summary>LOT 6 - reconcile the raw-slot needs with the resolver's view of the catalog. The
    /// planner only ever tells us a kind is STILL missing after looking at the canonical store too;
    /// a kind it does not return is already satisfied, so we suppress its scrape. This may only turn
    /// a need off (never on), keeping the behavior local-first without any under-scrape risk.</summary>
    private void ApplyResolverDrivenNeeds(MediaProjectionPlan plan, string systemId, string gameSlug, string? romPath)
    {
        var catalog = _catalog.BuildCatalog(systemId, gameSlug, romPath);
        var stillNeeded = _scrapePlanner
            .Plan(catalog, plan.Needs.Select(n => n.Kind), ScrapeNeedMode.MissingOnly)
            .Select(n => n.Kind)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        SuppressSatisfiedNeeds(plan.Needs, stillNeeded);
    }

    /// <summary>The merge rule (testable): a need stays missing only if it was already missing AND
    /// the resolver could not satisfy it. The planner can subtract a scrape, never add one.</summary>
    internal static void SuppressSatisfiedNeeds(IEnumerable<MediaNeed> needs, ISet<string> stillNeededKinds)
    {
        foreach (var need in needs)
        {
            need.IsMissing = need.IsMissing && stillNeededKinds.Contains(need.Kind);
        }
    }

    /// <summary>Splits a gamelist region/lang string ("USA, Europe") into distinct
    /// tokens. Fallback only - the compact referential is the primary source.</summary>
    private static List<string> SplitIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new List<string>();
        }

        return value
            .Split(IdentitySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        var existing = ReadSlotValue(request.Details, kind);

        existing = ToGamelistMediaPath(frontendSystemId, existing);

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

    /// <summary>
    /// Ce que la fiche porte deja pour ce type de media. La boite 2D vit dans la balise
    /// &lt;boxart&gt; des gamelists (ES, Data Pack) : ne lire que « box-2D », une cle que
    /// personne n'ecrit, faisait passer la vignette « boite 2D » pour vide a chaque visite.
    /// </summary>
    internal static string ReadSlotValue(GameDetails? details, string kind)
    {
        return kind switch
        {
            MediaKinds.Image => details?.Image ?? string.Empty,
            MediaKinds.Thumbnail => details?.Thumbnail ?? string.Empty,
            MediaKinds.Logo => details?.Extras.GetValueOrDefault("logo")
                ?? details?.Extras.GetValueOrDefault("wheel")
                ?? string.Empty,
            MediaKinds.Wheel => details?.Extras.GetValueOrDefault("wheel")
                ?? details?.Extras.GetValueOrDefault("logo")
                ?? string.Empty,
            MediaKinds.WheelCarbon => details?.Extras.GetValueOrDefault("wheel-carbon")
                ?? VisibleWheelOfStyle(details, "wheel-carbon")
                ?? string.Empty,
            MediaKinds.WheelSteel => details?.Extras.GetValueOrDefault("wheel-steel")
                ?? VisibleWheelOfStyle(details, "wheel-steel")
                ?? string.Empty,
            // ES uses <marquee> as the visible logo slot, so it may point to wheel/logo.
            // Do not reuse it as proof of a real ScreenScraper marquee asset.
            MediaKinds.Marquee => string.Empty,
            MediaKinds.ScreenMarquee => details?.Extras.GetValueOrDefault("screenmarquee") ?? string.Empty,
            MediaKinds.ScreenMarqueeSmall => details?.Extras.GetValueOrDefault("screenmarqueesmall") ?? string.Empty,
            MediaKinds.SteamGrid => details?.Extras.GetValueOrDefault("steamgrid") ?? string.Empty,
            MediaKinds.MixRbv1 => details?.Extras.GetValueOrDefault("mixrbv1") ?? string.Empty,
            MediaKinds.MixRbv2 => details?.Extras.GetValueOrDefault("mixrbv2")
                ?? details?.Extras.GetValueOrDefault("mix")
                ?? string.Empty,
            MediaKinds.BoxFront => details?.Extras.GetValueOrDefault("box-2D")
                ?? details?.Extras.GetValueOrDefault("boxart")
                ?? string.Empty,
            MediaKinds.BoxSide => details?.Extras.GetValueOrDefault("box-2D-side") ?? string.Empty,
            MediaKinds.BoxTexture => details?.Extras.GetValueOrDefault("box-texture") ?? string.Empty,
            MediaKinds.Box3d => details?.Extras.GetValueOrDefault("box-3D") ?? string.Empty,
            MediaKinds.Cartridge => details?.Extras.GetValueOrDefault("cartridge")
                ?? details?.Extras.GetValueOrDefault("support-2D")
                ?? string.Empty,
            MediaKinds.Label => details?.Extras.GetValueOrDefault("label")
                ?? details?.Extras.GetValueOrDefault("support-texture")
                ?? string.Empty,
            MediaKinds.Fanart => details?.Fanart ?? string.Empty,
            MediaKinds.Flyer => details?.Extras.GetValueOrDefault("flyer") ?? string.Empty,
            MediaKinds.Figurine => details?.Extras.GetValueOrDefault("figurine") ?? string.Empty,
            MediaKinds.Bezel => details?.Bezel ?? string.Empty,
            MediaKinds.BoxBack => details?.Boxback ?? string.Empty,
            MediaKinds.Map => details?.Map ?? details?.Extras.GetValueOrDefault("map") ?? string.Empty,
            MediaKinds.Manual => details?.Manual ?? string.Empty,
            MediaKinds.Magazine => details?.Extras.GetValueOrDefault("magazine") ?? string.Empty,
            MediaKinds.Video => details?.Video ?? string.Empty,
            MediaKinds.VideoNormalized => details?.Extras.GetValueOrDefault("video-normalized") ?? string.Empty,
            MediaKinds.ThemeHb => details?.Extras.GetValueOrDefault("themehb") ?? string.Empty,
            _ => string.Empty
        };
    }

    /// <summary>
    /// Logo regle sur « wheel-hd » : la gamelist n'a pas de balise par style, la roue choisie
    /// est dans &lt;marquee&gt; (l'emplacement visible) ou &lt;wheel&gt;. On la reconnait a son nom.
    /// </summary>
    private static string? VisibleWheelOfStyle(GameDetails? details, string style)
    {
        foreach (var value in new[] { details?.Marquee, details?.Extras.GetValueOrDefault("wheel") })
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                Path.GetFileNameWithoutExtension(value.Trim()).EndsWith(style, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Le watcher ES expose les medias du store canonique en URL « /api/v1/media/... » pour
    /// les clients REST. Ici on juge le disque : lue comme un chemin, l'URL tombait sur
    /// « E:\api\v1\media\... », chaque media du store passait pour absent a chaque visite,
    /// et la fiche etait repoussee a ES avec « Medias locaux appliques » (jeux du Data Pack,
    /// donc toute la collection World Scoring). On revient au chemin que porte la gamelist.
    /// Vide si l'URL sort du store.
    /// </summary>
    internal static string ToGamelistMediaPath(string frontendSystemId, string? value)
    {
        const string ApiMediaPrefix = "/api/v1/media/";
        var raw = value ?? string.Empty;
        if (!raw.StartsWith(ApiMediaPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return raw;
        }

        try
        {
            var mediaRoot = Path.GetFullPath(RetroBatPaths.MediaRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var relative = raw[ApiMediaPrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(mediaRoot, relative));
            if (!full.StartsWith(mediaRoot, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            var fromSystem = Path.GetRelativePath(Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId), full);
            return Path.IsPathRooted(fromSystem)
                ? fromSystem
                : "./" + fromSystem.Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Empty;
        }
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
