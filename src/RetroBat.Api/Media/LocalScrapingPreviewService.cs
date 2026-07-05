using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using RetroBat.Api.Controllers;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

public partial class LocalScrapingPreviewService
{
    private static readonly string[] DefaultPreferredRegions = ["fr", "eu", "wor", "us", "jp"];
    private static readonly string[] DefaultPreviewKinds =
    [
        MediaKinds.Image,
        MediaKinds.Thumbnail,
        MediaKinds.Wheel,
        MediaKinds.Marquee,
        MediaKinds.BoxFront,
        MediaKinds.Fanart,
        MediaKinds.Manual,
        MediaKinds.Video
    ];

    private static readonly string[] RomCandidateExtensions =
    {
        ".zip", ".7z", ".rar", ".iso", ".cue", ".chd", ".m3u", ".nes", ".sfc", ".smc", ".bin", ".md", ".gen",
        ".gba", ".gbc", ".gb", ".n64", ".z64", ".v64", ".nds", ".cdi", ".gdi", ".pbp", ".elf", ".exe", ".lnk"
    };

    private static readonly string[] SupportedMediaExtensions =
    {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".pdf", ".mp4", ".mkv", ".avi", ".webm", ".zip"
    };

    private readonly SystemIdNormalizer _systemIdNormalizer;
    private readonly GameNameNormalizer _gameNameNormalizer;
    private readonly MameGamelistGroupIndex _mameGamelistGroupIndex;
    private readonly LocalMediaIndexService _localMediaIndexService;
    private readonly ILogger<LocalScrapingPreviewService>? _logger;

    public LocalScrapingPreviewService(
        SystemIdNormalizer systemIdNormalizer,
        GameNameNormalizer gameNameNormalizer,
        MameGamelistGroupIndex mameGamelistGroupIndex,
        LocalMediaIndexService localMediaIndexService,
        ILogger<LocalScrapingPreviewService>? logger = null)
    {
        _systemIdNormalizer = systemIdNormalizer;
        _gameNameNormalizer = gameNameNormalizer;
        _mameGamelistGroupIndex = mameGamelistGroupIndex;
        _localMediaIndexService = localMediaIndexService;
        _logger = logger;
    }

    public Task<LocalScrapePreviewResponse> PreviewAsync(LocalScrapePreviewRequest request, CancellationToken cancellationToken = default)
    {
        var scope = ResolveScope(request);
        var preferredRegions = ResolvePreferredRegions(request);
        var mediaKinds = ResolveMediaKinds(request);
        var scan = _localMediaIndexService.Build(null, cancellationToken);
        var targets = ResolveTargets(request, scope, cancellationToken).ToList();

        var truncated = false;
        if (request.MaxGames > 0 && targets.Count > request.MaxGames)
        {
            targets = targets.Take(request.MaxGames).ToList();
            truncated = true;
        }

        var response = new LocalScrapePreviewResponse
        {
            Scope = scope,
            PreferredMediaRegions = preferredRegions,
            MediaKinds = mediaKinds,
            ScannedMediaFiles = scan.ScannedFiles,
            ParsedMediaFiles = scan.Candidates.Count,
            GamesEvaluated = targets.Count,
            Truncated = truncated,
            Systems = targets
                .Select(target => target.SystemId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(systemId => systemId, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        if (request.IncludeRootMediaPacks)
        {
            response.RootMediaPacks = ScanRootMediaPacks(request.MaxPackEntries, cancellationToken);
            response.RootMediaPackCount = response.RootMediaPacks.Count;
        }

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var gamePreview = new LocalScrapeGamePreview
            {
                SystemId = target.SystemId,
                MediaSystemId = target.MediaSystemId,
                GamePath = target.GamePath,
                GameName = target.GameName,
                GameSlug = target.GameSlug,
                FamilySlug = target.FamilySlug,
                RomRegion = target.RomRegion,
                IsArcadeLike = IsMameLikeSystem(target.SystemId)
            };

            foreach (var kind in mediaKinds)
            {
                var match = ResolveMedia(target, kind, preferredRegions, scan);
                if (match == null)
                {
                    response.MissingMediaSlots++;
                    if (request.IncludeMissing)
                    {
                        gamePreview.Media.Add(new LocalScrapeMediaPreview
                        {
                            Kind = kind,
                            Status = "missing-local",
                            Match = "missing",
                            TargetRelativePath = BuildTargetRelativePath(target, kind, ".png")
                        });
                    }

                    continue;
                }

                if (match.IsInherited)
                {
                    response.InheritedMatches++;
                }
                else
                {
                    response.ExactMatches++;
                }

                gamePreview.Media.Add(new LocalScrapeMediaPreview
                {
                    Kind = kind,
                    Status = match.IsInherited ? "resolved-local-inherited" : "resolved-local-exact",
                    Match = match.Match,
                    SourceRoot = match.SourceRoot,
                    Origin = match.Origin,
                    Region = match.Region,
                    SourcePath = match.Path,
                    SourceGameSlug = match.GameSlug,
                    TargetRelativePath = BuildTargetRelativePath(target, kind, Path.GetExtension(match.Path)),
                    VolatileTarget = true
                });
            }

            if (gamePreview.Media.Any(media => media.Status.StartsWith("resolved-", StringComparison.OrdinalIgnoreCase)))
            {
                response.GamesWithLocalMedia++;
            }

            response.Games.Add(gamePreview);
        }

        _logger?.LogInformation(
            "Local scraping preview completed: scope={Scope}, systems={SystemCount}, games={GameCount}, scanned={Scanned}, parsed={Parsed}, exact={Exact}, inherited={Inherited}, missing={Missing}",
            response.Scope,
            response.Systems.Count,
            response.GamesEvaluated,
            response.ScannedMediaFiles,
            response.ParsedMediaFiles,
            response.ExactMatches,
            response.InheritedMatches,
            response.MissingMediaSlots);

        return Task.FromResult(response);
    }

    private LocalMediaMatch? ResolveMedia(
        GamePreviewTarget target,
        string kind,
        IReadOnlyList<string> preferredRegions,
        LocalMediaIndex scan)
    {
        var candidates = scan.GetCandidates(target.MediaSystemId, kind);

        return SelectExact(candidates, target, preferredRegions)
            ?? SelectInherited(candidates, target, kind, preferredRegions);
    }

    private static LocalMediaMatch? SelectExact(
        IReadOnlyList<LocalMediaIndexCandidate> candidates,
        GamePreviewTarget target,
        IReadOnlyList<string> preferredRegions)
    {
        var exactCandidates = candidates
            .Where(candidate => string.Equals(candidate.GameSlug, target.GameSlug, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return SelectBestCandidate(exactCandidates, target.RomRegion, preferredRegions, isInherited: false, "exact");
    }

    private LocalMediaMatch? SelectInherited(
        IReadOnlyList<LocalMediaIndexCandidate> candidates,
        GamePreviewTarget target,
        string kind,
        IReadOnlyList<string> preferredRegions)
    {
        var inheritedCandidates = candidates
            .Where(candidate =>
                !string.Equals(candidate.GameSlug, target.GameSlug, StringComparison.OrdinalIgnoreCase) &&
                IsAllowedInheritedCandidate(candidate, target, kind, preferredRegions))
            .ToList();

        return SelectBestCandidate(inheritedCandidates, target.RomRegion, preferredRegions, isInherited: true, "inherited-from-family");
    }

    private bool IsAllowedInheritedCandidate(
        LocalMediaIndexCandidate candidate,
        GamePreviewTarget target,
        string kind,
        IReadOnlyList<string> preferredRegions)
    {
        if (!IsKindInheritanceAllowed(kind, candidate.Region, target.RomRegion, preferredRegions))
        {
            return false;
        }

        if (target.IsArcadeLike)
        {
            return target.RelatedSlugs.Contains(candidate.GameSlug, StringComparer.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(target.FamilySlug)
            && string.Equals(candidate.FamilySlug, target.FamilySlug, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKindInheritanceAllowed(
        string kind,
        string candidateRegion,
        string romRegion,
        IReadOnlyList<string> preferredRegions)
    {
        return MediaKinds.Normalize(kind) switch
        {
            MediaKinds.Manual => IsRegionCompatible(candidateRegion, romRegion, preferredRegions),
            MediaKinds.BoxFront or MediaKinds.BoxBack or MediaKinds.BoxSide => IsRegionCompatible(candidateRegion, romRegion, preferredRegions),
            MediaKinds.Video or MediaKinds.VideoNormalized => IsGenericRegion(candidateRegion),
            _ => true
        };
    }

    private static bool IsRegionCompatible(string candidateRegion, string romRegion, IReadOnlyList<string> preferredRegions)
    {
        return IsGenericRegion(candidateRegion)
            || preferredRegions.Contains(candidateRegion, StringComparer.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(romRegion) && string.Equals(candidateRegion, romRegion, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsGenericRegion(string region)
    {
        return string.IsNullOrWhiteSpace(region) || string.Equals(region, "wor", StringComparison.OrdinalIgnoreCase);
    }

    private static LocalMediaMatch? SelectBestCandidate(
        IReadOnlyList<LocalMediaIndexCandidate> candidates,
        string romRegion,
        IReadOnlyList<string> preferredRegions,
        bool isInherited,
        string match)
    {
        var selected = candidates
            .OrderBy(candidate => candidate.SourcePriority)
            .ThenBy(candidate => ScoreRegion(candidate.Region, romRegion, preferredRegions))
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (selected == null)
        {
            return null;
        }

        return new LocalMediaMatch(
            selected.Path,
            selected.SourceRoot,
            selected.Origin,
            selected.Region,
            selected.GameSlug,
            isInherited,
            match);
    }

    private static int ScoreRegion(string region, string romRegion, IReadOnlyList<string> preferredRegions)
    {
        if (string.IsNullOrWhiteSpace(region))
        {
            return 1000;
        }

        var preferredIndex = preferredRegions
            .Select((value, index) => new { value, index })
            .FirstOrDefault(item => string.Equals(item.value, region, StringComparison.OrdinalIgnoreCase));
        if (preferredIndex != null)
        {
            return preferredIndex.index;
        }

        if (!string.IsNullOrWhiteSpace(romRegion) &&
            string.Equals(region, romRegion, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        return string.Equals(region, "wor", StringComparison.OrdinalIgnoreCase) ? 200 : 500;
    }

    private List<LocalMediaPackPreview> ScanRootMediaPacks(int maxEntries, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(RetroBatPaths.PluginRoot))
        {
            return new List<LocalMediaPackPreview>();
        }

        return Directory.EnumerateFiles(RetroBatPaths.PluginRoot, "*.zip", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => PreviewRootMediaPack(path, Math.Max(0, maxEntries), cancellationToken))
            .ToList();
    }

    private LocalMediaPackPreview PreviewRootMediaPack(string path, int maxEntries, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        var preview = new LocalMediaPackPreview
        {
            FileName = fileInfo.Name,
            Path = fileInfo.FullName,
            SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            LastWriteTimeUtc = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTimeOffset.MinValue,
            DestinationHint = ResolvePackDestinationHint(fileInfo.Name)
        };

        try
        {
            using var archive = ZipFile.OpenRead(path);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                preview.TotalEntries++;

                if (IsUnsafeZipEntry(entry.FullName))
                {
                    preview.UnsafeEntries++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(entry.Name))
                {
                    continue;
                }

                if (!SupportedMediaExtensions.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                preview.MediaEntries++;
                if (_localMediaIndexService.TryParseMediaCandidateFromRelativePath(entry.FullName, preview.DestinationHint, 0, out var candidate))
                {
                    preview.ParsedMediaEntries++;
                    if (maxEntries == 0 || preview.SampleEntries.Count < maxEntries)
                    {
                        preview.SampleEntries.Add(new LocalMediaPackEntryPreview
                        {
                            EntryPath = entry.FullName.Replace('\\', '/'),
                            SystemId = candidate.SystemId,
                            GameSlug = candidate.GameSlug,
                            FamilySlug = candidate.FamilySlug,
                            Region = candidate.Region,
                            Kind = candidate.Kind,
                            Status = "parsed-media-entry"
                        });
                    }
                    else
                    {
                        preview.Truncated = true;
                    }
                }
            }

            preview.Status = ResolvePackStatus(preview);
            AddPackWarnings(preview);
            return preview;
        }
        catch (InvalidDataException ex)
        {
            preview.Status = "invalid-zip";
            preview.Warnings.Add(ex.Message);
            return preview;
        }
        catch (IOException ex)
        {
            preview.Status = "unreadable";
            preview.Warnings.Add(ex.Message);
            return preview;
        }
        catch (UnauthorizedAccessException ex)
        {
            preview.Status = "unreadable";
            preview.Warnings.Add(ex.Message);
            return preview;
        }
    }

    private static string ResolvePackDestinationHint(string fileName)
    {
        var normalized = fileName.ToLowerInvariant();
        return normalized.Contains("user", StringComparison.OrdinalIgnoreCase)
            ? "media/user"
            : "media";
    }

    private static string ResolvePackStatus(LocalMediaPackPreview preview)
    {
        if (preview.UnsafeEntries > 0)
        {
            return "unsafe-entries";
        }

        if (preview.TotalEntries == 0)
        {
            return "empty";
        }

        if (preview.MediaEntries == 0)
        {
            return "no-media-entries";
        }

        return preview.ParsedMediaEntries == 0
            ? "no-parsed-media-entries"
            : "ready-for-staging";
    }

    private static void AddPackWarnings(LocalMediaPackPreview preview)
    {
        if (preview.UnsafeEntries > 0)
        {
            preview.Warnings.Add("Archive contains absolute paths or parent traversal entries and must be rejected before extraction.");
        }

        if (preview.MediaEntries > 0 && preview.ParsedMediaEntries == 0)
        {
            preview.Warnings.Add("Archive contains media files, but none matched the local media naming rules.");
        }
    }

    private static bool IsUnsafeZipEntry(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return true;
        }

        var normalized = entryName.Replace('\\', '/').Trim();
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Contains(":/", StringComparison.Ordinal) ||
            normalized.Contains("../", StringComparison.Ordinal) ||
            normalized.Equals("..", StringComparison.Ordinal))
        {
            return true;
        }

        return normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals("..", StringComparison.Ordinal));
    }

    private IEnumerable<GamePreviewTarget> ResolveTargets(
        LocalScrapePreviewRequest request,
        string scope,
        CancellationToken cancellationToken)
    {
        return scope switch
        {
            "game" => ResolveSingleGame(request, cancellationToken),
            "system" => ResolveSystemGames(RequireSystemId(request), cancellationToken),
            "all" => ResolveAllSystemGames(cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported local scrape preview scope: {scope}")
        };
    }

    private IEnumerable<GamePreviewTarget> ResolveSingleGame(LocalScrapePreviewRequest request, CancellationToken cancellationToken)
    {
        var systemId = RequireSystemId(request);
        if (string.IsNullOrWhiteSpace(request.GamePath))
        {
            throw new InvalidOperationException("GamePath is required for scope=game.");
        }

        var frontendSystemId = _systemIdNormalizer.NormalizeFrontend(systemId);
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId);
        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        XElement? matchedGameNode = null;
        if (File.Exists(gamelistPath))
        {
            matchedGameNode = LoadGamelistDocumentOrEmpty(gamelistPath).Root?
                .Elements("game")
                .FirstOrDefault(node => MatchesGameNode(node, request.GamePath, request.GameName, systemRoot));
        }

        var gamePath = ResolveAbsoluteGamePath(request.GamePath, systemRoot, matchedGameNode);
        yield return BuildTarget(frontendSystemId, systemRoot, matchedGameNode, gamePath, request.GameName, cancellationToken);
    }

    private IEnumerable<GamePreviewTarget> ResolveSystemGames(string systemId, CancellationToken cancellationToken)
    {
        var frontendSystemId = _systemIdNormalizer.NormalizeFrontend(systemId);
        var systemRoot = Path.Combine(RetroBatPaths.RomsRoot, frontendSystemId);
        if (!Directory.Exists(systemRoot))
        {
            throw new InvalidOperationException($"System roms directory does not exist: {systemRoot}");
        }

        var gamelistPath = Path.Combine(systemRoot, "gamelist.xml");
        if (File.Exists(gamelistPath))
        {
            var document = LoadGamelistDocumentOrEmpty(gamelistPath);
            foreach (var gameNode in document.Root?.Elements("game") ?? Enumerable.Empty<XElement>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawPath = gameNode.Element("path")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    continue;
                }

                yield return BuildTarget(frontendSystemId, systemRoot, gameNode, ResolveAbsolutePath(systemRoot, rawPath), null, cancellationToken);
            }

            yield break;
        }

        foreach (var romPath in EnumerateRomCandidates(systemRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return BuildTarget(frontendSystemId, systemRoot, null, romPath, null, cancellationToken);
        }
    }

    private IEnumerable<GamePreviewTarget> ResolveAllSystemGames(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(RetroBatPaths.RomsRoot))
        {
            yield break;
        }

        foreach (var systemDirectory in Directory.GetDirectories(RetroBatPaths.RomsRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var systemId = Path.GetFileName(systemDirectory);
            if (string.IsNullOrWhiteSpace(systemId))
            {
                continue;
            }

            foreach (var game in ResolveSystemGames(systemId, cancellationToken))
            {
                yield return game;
            }
        }
    }

    private GamePreviewTarget BuildTarget(
        string frontendSystemId,
        string systemRoot,
        XElement? gameNode,
        string absoluteGamePath,
        string? requestedName,
        CancellationToken cancellationToken)
    {
        var gameName = gameNode?.Element("name")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(gameName))
        {
            gameName = !string.IsNullOrWhiteSpace(requestedName)
                ? requestedName.Trim()
                : Path.GetFileNameWithoutExtension(absoluteGamePath);
        }

        var rawGamePath = gameNode?.Element("path")?.Value?.Trim();
        var gamePath = string.IsNullOrWhiteSpace(rawGamePath)
            ? absoluteGamePath
            : rawGamePath;
        var gameSlug = _gameNameNormalizer.NormalizeGameSlug(gameName, absoluteGamePath);
        if (string.IsNullOrWhiteSpace(gameSlug))
        {
            gameSlug = NormalizeSlug(Path.GetFileNameWithoutExtension(absoluteGamePath));
        }

        var familySlug = BuildFamilySlug(Path.GetFileNameWithoutExtension(absoluteGamePath));
        var romRegion = ExtractRegion(Path.GetFileNameWithoutExtension(absoluteGamePath));
        var mediaSystemId = _systemIdNormalizer.Normalize(frontendSystemId);
        var relatedSlugs = IsMameLikeSystem(frontendSystemId)
            ? _mameGamelistGroupIndex.GetRelatedRoms(frontendSystemId, gamePath)
                .Select(NormalizeSlug)
                .Where(slug => !string.IsNullOrWhiteSpace(slug))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (relatedSlugs.Count == 0)
        {
            relatedSlugs.Add(gameSlug);
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new GamePreviewTarget(
            frontendSystemId,
            mediaSystemId,
            gamePath,
            gameName ?? string.Empty,
            gameSlug,
            familySlug,
            romRegion,
            IsMameLikeSystem(frontendSystemId),
            relatedSlugs);
    }

    private static string BuildTargetRelativePath(GamePreviewTarget target, string kind, string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = kind switch
            {
                MediaKinds.Manual => ".pdf",
                MediaKinds.Video or MediaKinds.VideoNormalized => ".mp4",
                MediaKinds.ThemeHb => ".zip",
                _ => ".png"
            };
        }

        var directory = kind switch
        {
            MediaKinds.Manual => "manuals",
            MediaKinds.Video or MediaKinds.VideoNormalized => "videos",
            MediaKinds.ThemeHb => "themes",
            _ => "images"
        };

        return $"{directory}/{Path.GetFileNameWithoutExtension(target.GamePath)}{ProjectionSuffix(kind)}{extension}".Replace('\\', '/');
    }

    private static string ProjectionSuffix(string kind)
    {
        return kind switch
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
            MediaKinds.Video => "-video",
            MediaKinds.VideoNormalized => "-video-normalized",
            MediaKinds.ThemeHb => "-themehb",
            _ => "-" + kind.Replace(' ', '-')
        };
    }

    private static IEnumerable<string> EnumerateRomCandidates(string systemRoot)
    {
        return Directory.EnumerateFiles(systemRoot, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path => RomCandidateExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveScope(LocalScrapePreviewRequest request)
    {
        var requestedScope = (request.Scope ?? string.Empty).Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(requestedScope))
        {
            return requestedScope switch
            {
                "game" or "entry" => "game",
                "system" or "systeme" => "system",
                "all" or "systems" or "tous" => "all",
                _ => throw new InvalidOperationException("Scope must be one of: game, system, all.")
            };
        }

        if (!string.IsNullOrWhiteSpace(request.GamePath))
        {
            return "game";
        }

        return string.IsNullOrWhiteSpace(request.SystemId) ? "all" : "system";
    }

    private static List<string> ResolvePreferredRegions(LocalScrapePreviewRequest request)
    {
        IEnumerable<string> values = request.PreferredMediaRegions.Count > 0
            ? request.PreferredMediaRegions
            : DefaultPreferredRegions;

        return values
            .Select(NormalizeRegion)
            .Where(region => !string.IsNullOrWhiteSpace(region))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ResolveMediaKinds(LocalScrapePreviewRequest request)
    {
        IEnumerable<string> values = request.MediaKinds.Count > 0
            ? request.MediaKinds
            : DefaultPreviewKinds;

        return values
            .Select(MediaKinds.Normalize)
            .Where(kind => !string.IsNullOrWhiteSpace(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string RequireSystemId(LocalScrapePreviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SystemId))
        {
            throw new InvalidOperationException("SystemId is required for this local scrape preview scope.");
        }

        return request.SystemId.Trim();
    }

    private static string ResolveAbsoluteGamePath(string requestedPath, string systemRoot, XElement? matchedGameNode)
    {
        if (Path.IsPathRooted(requestedPath))
        {
            return requestedPath;
        }

        var matchedPath = matchedGameNode?.Element("path")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(matchedPath))
        {
            return ResolveAbsolutePath(systemRoot, matchedPath);
        }

        return ResolveAbsolutePath(systemRoot, requestedPath);
    }

    private static string ResolveAbsolutePath(string systemRoot, string path)
    {
        var normalized = (path ?? string.Empty).Trim().Replace('/', Path.DirectorySeparatorChar);
        if (normalized.StartsWith("." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return Path.GetFullPath(Path.Combine(systemRoot, normalized));
    }

    private static XDocument LoadGamelistDocumentOrEmpty(string gamelistPath)
    {
        try
        {
            using var stream = new FileStream(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex) when (ex.Message.Contains("Root element is missing", StringComparison.OrdinalIgnoreCase))
        {
            return new XDocument(new XDeclaration("1.0", "utf-8", "yes"), new XElement("gameList"));
        }
    }

    private static bool MatchesGameNode(XElement gameNode, string requestedPath, string? requestedName, string systemRoot)
    {
        var gamePath = gameNode.Element("path")?.Value?.Trim();
        var gameName = gameNode.Element("name")?.Value?.Trim();
        var normalizedRequestedPath = NormalizeCompareValue(requestedPath);
        var requestedFileName = NormalizeCompareValue(Path.GetFileName(requestedPath));
        var requestedNameValue = NormalizeCompareValue(requestedName);

        if (!string.IsNullOrWhiteSpace(gamePath))
        {
            var absoluteGamePath = ResolveAbsolutePath(systemRoot, gamePath);
            var candidates = new[]
            {
                NormalizeCompareValue(gamePath),
                NormalizeCompareValue(absoluteGamePath),
                NormalizeCompareValue(Path.GetFileName(gamePath)),
                NormalizeCompareValue(Path.GetFileName(absoluteGamePath))
            };

            if (candidates.Contains(normalizedRequestedPath, StringComparer.OrdinalIgnoreCase)
                || candidates.Contains(requestedFileName, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return !string.IsNullOrWhiteSpace(requestedNameValue)
            && string.Equals(NormalizeCompareValue(gameName), requestedNameValue, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCompareValue(string? value)
    {
        return (value ?? string.Empty)
            .Trim()
            .Replace('\\', '/')
            .ToLowerInvariant();
    }

    private static string BuildFamilySlug(string? value)
    {
        var fileName = Path.GetFileNameWithoutExtension(value ?? string.Empty);
        var cleaned = VariantTagRegex().Replace(fileName, " ");
        return NormalizeSlug(cleaned);
    }

    private static string ExtractRegion(string? value)
    {
        var input = value ?? string.Empty;
        foreach (Match match in BracketContentRegex().Matches(input))
        {
            var region = NormalizeRegion(match.Groups["value"].Value);
            if (!string.IsNullOrWhiteSpace(region))
            {
                return region;
            }
        }

        return string.Empty;
    }

    private static string NormalizeSlug(string? value)
    {
        var cleaned = (value ?? string.Empty).Trim().ToLowerInvariant();
        cleaned = cleaned.Replace('&', ' ');
        cleaned = NonAlphaNumericRegex().Replace(cleaned, " ");
        cleaned = MultiSpaceRegex().Replace(cleaned, " ").Trim();
        return cleaned.Replace(' ', '_');
    }

    private static string NormalizeRegion(string? value)
    {
        var normalized = NormalizeSlug(value).Replace("_", string.Empty);
        return normalized switch
        {
            "fr" or "fra" or "fre" or "french" or "france" => "fr",
            "eu" or "eur" or "europe" => "eu",
            "us" or "usa" or "america" => "us",
            "jp" or "jpn" or "japan" => "jp",
            "world" or "wor" or "ww" => "wor",
            "uk" or "gb" => "uk",
            "de" or "ger" or "germany" => "de",
            "es" or "spa" or "spain" => "es",
            "it" or "ita" or "italy" => "it",
            _ when normalized.Length is >= 2 and <= 3 => normalized,
            _ => string.Empty
        };
    }

    private static bool IsMameLikeSystem(string systemId)
    {
        return (systemId ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "arcade" or "mame" or "mame64" or "fbneo" or "fba" or "hbmame" => true,
            _ => false
        };
    }

    [GeneratedRegex(@"\((?<value>[^)]*)\)|\[(?<value>[^\]]*)\]", RegexOptions.Compiled)]
    private static partial Regex BracketContentRegex();

    [GeneratedRegex(@"\((Europe|USA|Japan|World|France|Rev[^)]*|Beta|Proto|Prototype|Demo|Sample|Unl|Unlicensed)[^)]*\)|\[(T\+[^]]+|Rev[^]]*|Beta|Proto|Demo|Sample|Unl|Unlicensed)[^]]*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VariantTagRegex();

    [GeneratedRegex(@"[^a-zA-Z0-9]+", RegexOptions.Compiled)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

    private sealed record LocalMediaMatch(
        string Path,
        string SourceRoot,
        string Origin,
        string Region,
        string GameSlug,
        bool IsInherited,
        string Match);

    private sealed record GamePreviewTarget(
        string SystemId,
        string MediaSystemId,
        string GamePath,
        string GameName,
        string GameSlug,
        string FamilySlug,
        string RomRegion,
        bool IsArcadeLike,
        HashSet<string> RelatedSlugs);

}
