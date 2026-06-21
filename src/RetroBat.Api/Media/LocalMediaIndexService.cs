using System.Text.RegularExpressions;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Media;

public partial class LocalMediaIndexService
{
    private static readonly AsyncLocal<LocalMediaIndex?> ActiveIndex = new();

    private static readonly string[] SupportedMediaExtensions =
    [
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".pdf", ".mp4", ".mkv", ".avi", ".webm", ".zip"
    ];

    private static readonly Dictionary<string, string> KindSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image"] = MediaKinds.Image,
        ["sstitle"] = MediaKinds.Image,
        ["screentitle"] = MediaKinds.Image,
        ["screen-title"] = MediaKinds.Image,
        ["title"] = MediaKinds.Image,
        ["thumb"] = MediaKinds.Thumbnail,
        ["thumbnail"] = MediaKinds.Thumbnail,
        ["ss"] = MediaKinds.Thumbnail,
        ["screenshot"] = MediaKinds.Thumbnail,
        ["logo"] = MediaKinds.Wheel,
        ["wheel"] = MediaKinds.Wheel,
        ["wheelcarbon"] = MediaKinds.WheelCarbon,
        ["wheel-carbon"] = MediaKinds.WheelCarbon,
        ["wheelsteel"] = MediaKinds.WheelSteel,
        ["wheel-steel"] = MediaKinds.WheelSteel,
        ["marquee"] = MediaKinds.Marquee,
        ["screenmarquee"] = MediaKinds.ScreenMarquee,
        ["screen-marquee"] = MediaKinds.ScreenMarquee,
        ["screenmarqueesmall"] = MediaKinds.ScreenMarqueeSmall,
        ["screen-marquee-small"] = MediaKinds.ScreenMarqueeSmall,
        ["steamgrid"] = MediaKinds.SteamGrid,
        ["mix"] = MediaKinds.MixRbv2,
        ["mixrbv1"] = MediaKinds.MixRbv1,
        ["mixrbv2"] = MediaKinds.MixRbv2,
        ["box2d"] = MediaKinds.BoxFront,
        ["box-2d"] = MediaKinds.BoxFront,
        ["boxfront"] = MediaKinds.BoxFront,
        ["box-front"] = MediaKinds.BoxFront,
        ["boxside"] = MediaKinds.BoxSide,
        ["box-side"] = MediaKinds.BoxSide,
        ["boxtexture"] = MediaKinds.BoxTexture,
        ["box-texture"] = MediaKinds.BoxTexture,
        ["box3d"] = MediaKinds.Box3d,
        ["box-3d"] = MediaKinds.Box3d,
        ["cartridge"] = MediaKinds.Cartridge,
        ["cart"] = MediaKinds.Cartridge,
        ["support2d"] = MediaKinds.Cartridge,
        ["support-2d"] = MediaKinds.Cartridge,
        ["label"] = MediaKinds.Label,
        ["supporttexture"] = MediaKinds.Label,
        ["support-texture"] = MediaKinds.Label,
        ["fanart"] = MediaKinds.Fanart,
        ["flyer"] = MediaKinds.Flyer,
        ["figurine"] = MediaKinds.Figurine,
        ["bezel"] = MediaKinds.Bezel,
        ["boxback"] = MediaKinds.BoxBack,
        ["box-back"] = MediaKinds.BoxBack,
        ["map"] = MediaKinds.Map,
        ["manual"] = MediaKinds.Manual,
        ["magazine"] = MediaKinds.Magazine,
        ["video"] = MediaKinds.Video,
        ["video-normalized"] = MediaKinds.VideoNormalized,
        ["videonormalized"] = MediaKinds.VideoNormalized,
        ["themehb"] = MediaKinds.ThemeHb
    };

    private readonly SystemIdNormalizer _systemIdNormalizer;

    public LocalMediaIndexService(SystemIdNormalizer systemIdNormalizer)
    {
        _systemIdNormalizer = systemIdNormalizer;
    }

    public IDisposable BeginSystemScope(string systemId, CancellationToken cancellationToken = default)
    {
        var previous = ActiveIndex.Value;
        ActiveIndex.Value = Build([systemId], cancellationToken);
        return new LocalMediaIndexLease(previous);
    }

    public LocalMediaIndex Build(IEnumerable<string>? systemIds = null, CancellationToken cancellationToken = default)
    {
        var requestedSystems = systemIds?
            .Select(_systemIdNormalizer.Normalize)
            .Where(systemId => !string.IsNullOrWhiteSpace(systemId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<LocalMediaIndexCandidate>();
        var scannedFiles = 0;
        ScanRoot(RetroBatPaths.MediaUserSystemsRoot, "media/user", 0, requestedSystems, candidates, ref scannedFiles, cancellationToken);
        ScanRoot(RetroBatPaths.MediaSystemsRoot, "media", 1, requestedSystems, candidates, ref scannedFiles, cancellationToken);
        return new LocalMediaIndex(scannedFiles, candidates);
    }

    public string? ResolveActiveSourcePath(string systemId, string gameSlug, string kind)
    {
        return ActiveIndex.Value?.ResolveExact(systemId, gameSlug, kind)?.Path;
    }

    public string? ResolveActiveSourcePath(string sourceRoot, string systemId, string gameSlug, string kind)
    {
        return ActiveIndex.Value?.ResolveExact(systemId, gameSlug, kind, sourceRoot)?.Path;
    }

    public bool TryParseMediaCandidateFromRelativePath(
        string relative,
        string sourceRoot,
        int sourcePriority,
        out LocalMediaIndexCandidate candidate,
        string? physicalPath = null)
    {
        candidate = default!;
        var normalizedRelative = NormalizeMediaRelativePath(relative);
        var parts = normalizedRelative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/')
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        if (parts.Length < 2)
        {
            return false;
        }

        var systemId = _systemIdNormalizer.Normalize(parts[0]);
        if (string.IsNullOrWhiteSpace(systemId))
        {
            return false;
        }

        var fileStem = Path.GetFileNameWithoutExtension(parts[^1]);
        var directoryParts = parts.Take(parts.Length - 1).ToArray();
        var gameSlug = ResolveGameSlugFromPath(parts, fileStem);
        if (string.IsNullOrWhiteSpace(gameSlug))
        {
            return false;
        }

        var parsed = TryParseRegionalKind(fileStem, gameSlug)
            ?? TryParsePathKind(directoryParts, fileStem);
        if (parsed == null)
        {
            return false;
        }

        candidate = new LocalMediaIndexCandidate(
            systemId,
            gameSlug,
            BuildFamilySlug(gameSlug),
            parsed.Region,
            parsed.Kind,
            physicalPath ?? normalizedRelative.Replace('\\', '/'),
            sourceRoot,
            sourceRoot == "media/user" ? "user-pack" : "local-pack",
            sourcePriority);
        return true;
    }

    private void ScanRoot(
        string root,
        string sourceRoot,
        int sourcePriority,
        IReadOnlySet<string>? requestedSystems,
        List<LocalMediaIndexCandidate> candidates,
        ref int scannedFiles,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var systemRoots = requestedSystems is { Count: > 0 }
            ? requestedSystems.Select(systemId => Path.Combine(root, systemId)).Where(Directory.Exists)
            : Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly);

        foreach (var systemRoot in systemRoots)
        {
            foreach (var path in Directory.EnumerateFiles(systemRoot, "*.*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSupportedMediaExtension(path))
                {
                    continue;
                }

                scannedFiles++;
                var relative = Path.GetRelativePath(root, path);
                if (TryParseMediaCandidateFromRelativePath(relative, sourceRoot, sourcePriority, out var candidate, path))
                {
                    candidates.Add(candidate);
                }
            }
        }
    }

    private static string NormalizeMediaRelativePath(string relative)
    {
        var normalized = (relative ?? string.Empty).Replace('\\', '/').TrimStart('/');
        foreach (var prefix in new[] { "media/user/systems/", "media/systems/", "systems/", "media/user/", "media/" })
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return normalized[prefix.Length..];
            }
        }

        return normalized;
    }

    private static string ResolveGameSlugFromPath(IReadOnlyList<string> parts, string fileStem)
    {
        for (var i = 0; i < parts.Count - 1; i++)
        {
            if (string.Equals(parts[i], "games", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Count - 1)
            {
                return NormalizeSlug(parts[i + 1]);
            }
        }

        var parsedFromFile = TryParseRegionalKind(fileStem, string.Empty);
        return parsedFromFile != null && !string.IsNullOrWhiteSpace(parsedFromFile.GameSlugFromFile)
            ? parsedFromFile.GameSlugFromFile
            : string.Empty;
    }

    private static ParsedMediaName? TryParseRegionalKind(string fileStem, string expectedGameSlug)
    {
        foreach (var suffix in KindSuffixes.Keys.OrderByDescending(key => key.Length))
        {
            if (!fileStem.EndsWith("-" + suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var beforeKind = fileStem[..^(suffix.Length + 1)];
            var region = string.Empty;
            var gamePart = beforeKind;
            var regionMatch = RegionSuffixRegex().Match(beforeKind);
            if (regionMatch.Success)
            {
                region = NormalizeRegion(regionMatch.Groups["region"].Value);
                gamePart = beforeKind[..^(regionMatch.Groups["region"].Value.Length + 1)];
            }

            if (!KindSuffixes.TryGetValue(suffix, out var kind))
            {
                continue;
            }

            var parsedGameSlug = NormalizeSlug(gamePart);
            if (!string.IsNullOrWhiteSpace(expectedGameSlug) &&
                !string.Equals(parsedGameSlug, expectedGameSlug, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(NormalizeSlug(fileStem), expectedGameSlug, StringComparison.OrdinalIgnoreCase))
            {
                return new ParsedMediaName(kind, region, string.Empty);
            }

            return new ParsedMediaName(kind, region, parsedGameSlug);
        }

        return null;
    }

    private static ParsedMediaName? TryParsePathKind(IReadOnlyList<string> directoryParts, string fileStem)
    {
        var normalizedStem = fileStem.Trim().ToLowerInvariant();
        var directory = string.Join("/", directoryParts).Replace('\\', '/').ToLowerInvariant();

        if (directory.EndsWith("/artwork", StringComparison.OrdinalIgnoreCase))
        {
            var (baseStem, region) = SplitLocalizedCanonicalStem(normalizedStem);
            return baseStem switch
            {
                "screentitle" or "screen-title" or "sstitle" or "title" or "image" => new ParsedMediaName(MediaKinds.Image, region, string.Empty),
                "screenshot" or "ss" or "thumb" or "thumbnail" => new ParsedMediaName(MediaKinds.Thumbnail, region, string.Empty),
                "cartridge" => new ParsedMediaName(MediaKinds.Cartridge, region, string.Empty),
                "label" => new ParsedMediaName(MediaKinds.Label, region, string.Empty),
                "fanart" => new ParsedMediaName(MediaKinds.Fanart, region, string.Empty),
                "flyer" => new ParsedMediaName(MediaKinds.Flyer, region, string.Empty),
                "figurine" => new ParsedMediaName(MediaKinds.Figurine, region, string.Empty),
                _ => null
            };
        }

        if (directory.Contains("/artwork/fanart", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(normalizedStem, "fanart", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedMediaName(MediaKinds.Fanart, string.Empty, string.Empty);
        }

        if (directory.Contains("/artwork/flyers", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(normalizedStem, "flyer", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedMediaName(MediaKinds.Flyer, string.Empty, string.Empty);
        }

        if (directory.Contains("/artwork/marquee", StringComparison.OrdinalIgnoreCase))
        {
            var (baseStem, region) = SplitLocalizedCanonicalStem(normalizedStem);
            return baseStem switch
            {
                "marquee" => new ParsedMediaName(MediaKinds.Marquee, region, string.Empty),
                "screenmarquee" => new ParsedMediaName(MediaKinds.ScreenMarquee, region, string.Empty),
                "screenmarquee-small" or "screenmarqueesmall" => new ParsedMediaName(MediaKinds.ScreenMarqueeSmall, region, string.Empty),
                _ => null
            };
        }

        if (directory.Contains("/artwork/screenmarquee", StringComparison.OrdinalIgnoreCase))
        {
            var (baseStem, region) = SplitLocalizedCanonicalStem(normalizedStem);
            return baseStem switch
            {
                "screenmarquee" => new ParsedMediaName(MediaKinds.ScreenMarquee, region, string.Empty),
                "screenmarquee-small" or "screenmarqueesmall" => new ParsedMediaName(MediaKinds.ScreenMarqueeSmall, region, string.Empty),
                _ => null
            };
        }

        if (directory.EndsWith("/ui", StringComparison.OrdinalIgnoreCase) ||
            directory.Contains("/ui/steamgrid", StringComparison.OrdinalIgnoreCase) ||
            directory.Contains("/artwork/steamgrid", StringComparison.OrdinalIgnoreCase))
        {
            var (baseStem, region) = SplitLocalizedCanonicalStem(normalizedStem);
            return baseStem switch
            {
                "steamgrid" => new ParsedMediaName(MediaKinds.SteamGrid, region, string.Empty),
                _ => null
            };
        }

        if (directory.Contains("/artwork/figurines", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(normalizedStem, "figurine", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedMediaName(MediaKinds.Figurine, string.Empty, string.Empty);
        }

        if (directory.Contains("/artwork/box", StringComparison.OrdinalIgnoreCase))
        {
            var (baseStem, region) = SplitLocalizedCanonicalStem(normalizedStem);
            return baseStem switch
            {
                "front" => new ParsedMediaName(MediaKinds.BoxFront, region, string.Empty),
                "back" => new ParsedMediaName(MediaKinds.BoxBack, region, string.Empty),
                "side" => new ParsedMediaName(MediaKinds.BoxSide, region, string.Empty),
                "texture" => new ParsedMediaName(MediaKinds.BoxTexture, region, string.Empty),
                "3d" => new ParsedMediaName(MediaKinds.Box3d, region, string.Empty),
                _ => null
            };
        }

        return KindSuffixes.TryGetValue(normalizedStem, out var kind)
            ? new ParsedMediaName(kind, string.Empty, string.Empty)
            : null;
    }

    private static (string BaseStem, string Region) SplitLocalizedCanonicalStem(string normalizedStem)
    {
        var match = RegionSuffixRegex().Match(normalizedStem);
        if (!match.Success)
        {
            return (normalizedStem, string.Empty);
        }

        var region = NormalizeRegion(match.Groups["region"].Value);
        var baseStem = normalizedStem[..^(match.Groups["region"].Value.Length + 1)];
        return (baseStem, region);
    }

    private static int ScoreCandidatePath(string path)
    {
        var extension = Path.GetExtension(path).Trim().ToLowerInvariant();
        return extension switch
        {
            ".png" => 0,
            ".jpg" => 1,
            ".jpeg" => 2,
            ".webp" => 3,
            ".gif" => 4,
            ".mp4" => 5,
            ".pdf" => 6,
            ".zip" => 7,
            _ => 50
        };
    }

    private static bool IsSupportedMediaExtension(string path)
    {
        return SupportedMediaExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private static string BuildFamilySlug(string? value)
    {
        var fileName = Path.GetFileNameWithoutExtension(value ?? string.Empty);
        var cleaned = VariantTagRegex().Replace(fileName, " ");
        return NormalizeSlug(cleaned);
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

    [GeneratedRegex(@"-(?<region>[a-zA-Z]{2,5}|world|europe|usa|japan|france)$", RegexOptions.Compiled)]
    private static partial Regex RegionSuffixRegex();

    [GeneratedRegex(@"\((Europe|USA|Japan|World|France|Rev[^)]*|Beta|Proto|Prototype|Demo|Sample|Unl|Unlicensed)[^)]*\)|\[(T\+[^]]+|Rev[^]]*|Beta|Proto|Demo|Sample|Unl|Unlicensed)[^]]*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex VariantTagRegex();

    [GeneratedRegex(@"[^a-zA-Z0-9]+", RegexOptions.Compiled)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();

    private sealed class LocalMediaIndexLease : IDisposable
    {
        private readonly LocalMediaIndex? _previous;
        private bool _disposed;

        public LocalMediaIndexLease(LocalMediaIndex? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            ActiveIndex.Value = _previous;
            _disposed = true;
        }
    }

    private sealed record ParsedMediaName(string Kind, string Region, string GameSlugFromFile);
}

public sealed class LocalMediaIndex
{
    private readonly Dictionary<string, List<LocalMediaIndexCandidate>> _bySystemKind;

    public LocalMediaIndex(int scannedFiles, IReadOnlyList<LocalMediaIndexCandidate> candidates)
    {
        ScannedFiles = scannedFiles;
        Candidates = candidates;
        _bySystemKind = candidates
            .GroupBy(candidate => BuildSystemKindKey(candidate.SystemId, candidate.Kind), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(candidate => candidate.SourcePriority)
                    .ThenBy(candidate => ScorePath(candidate.Path))
                    .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    public int ScannedFiles { get; }
    public IReadOnlyList<LocalMediaIndexCandidate> Candidates { get; }

    public IReadOnlyList<LocalMediaIndexCandidate> GetCandidates(string systemId, string kind)
    {
        return _bySystemKind.TryGetValue(BuildSystemKindKey(systemId, kind), out var candidates)
            ? candidates
            : [];
    }

    public LocalMediaIndexCandidate? ResolveExact(string systemId, string gameSlug, string kind, string? sourceRoot = null)
    {
        return GetCandidates(systemId, kind)
            .Where(candidate =>
                string.Equals(candidate.GameSlug, gameSlug, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(sourceRoot) || string.Equals(candidate.SourceRoot, sourceRoot, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault();
    }

    public LocalMediaIndexCandidate? ResolveBest(
        string systemId,
        string gameSlug,
        string familySlug,
        string kind,
        string preferredRegion = "")
    {
        return ResolveBest(systemId, gameSlug, familySlug, kind, [preferredRegion]);
    }

    public LocalMediaIndexCandidate? ResolveBest(
        string systemId,
        string gameSlug,
        string familySlug,
        string kind,
        IReadOnlyList<string> preferredRegions)
    {
        var normalizedGameSlug = (gameSlug ?? string.Empty).Trim();
        var normalizedFamilySlug = (familySlug ?? string.Empty).Trim();
        var candidates = GetCandidates(systemId, kind);
        var normalizedRegions = NormalizeCandidateRegions(preferredRegions);

        return PickBestRegionalCandidate(
                candidates.Where(candidate => string.Equals(candidate.GameSlug, normalizedGameSlug, StringComparison.OrdinalIgnoreCase)),
                normalizedRegions)
            ?? PickBestRegionalCandidate(
                candidates.Where(candidate =>
                    !string.IsNullOrWhiteSpace(normalizedFamilySlug) &&
                    string.Equals(candidate.FamilySlug, normalizedFamilySlug, StringComparison.OrdinalIgnoreCase)),
                normalizedRegions);
    }

    private static LocalMediaIndexCandidate? PickBestRegionalCandidate(
        IEnumerable<LocalMediaIndexCandidate> candidates,
        IReadOnlyList<string> preferredRegions)
    {
        var list = candidates.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        foreach (var preferredRegion in preferredRegions)
        {
            var localized = list.FirstOrDefault(candidate =>
                string.Equals(candidate.Region, preferredRegion, StringComparison.OrdinalIgnoreCase));
            if (localized != null)
            {
                return localized;
            }
        }

        return list.FirstOrDefault(candidate => string.IsNullOrWhiteSpace(candidate.Region))
            ?? list.FirstOrDefault(candidate => string.Equals(candidate.Region, "wor", StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault();
    }

    private static string BuildSystemKindKey(string systemId, string kind)
    {
        return $"{systemId.Trim().ToLowerInvariant()}|{MediaKinds.Normalize(kind)}";
    }

    private static string NormalizeCandidateRegion(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
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

    private static IReadOnlyList<string> NormalizeCandidateRegions(IReadOnlyList<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Select(NormalizeCandidateRegion)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int ScorePath(string path)
    {
        return Path.GetExtension(path).Trim().ToLowerInvariant() switch
        {
            ".png" => 0,
            ".jpg" => 1,
            ".jpeg" => 2,
            ".webp" => 3,
            ".gif" => 4,
            ".mp4" => 5,
            ".pdf" => 6,
            ".zip" => 7,
            _ => 50
        };
    }
}

public sealed record LocalMediaIndexCandidate(
    string SystemId,
    string GameSlug,
    string FamilySlug,
    string Region,
    string Kind,
    string Path,
    string SourceRoot,
    string Origin,
    int SourcePriority);
