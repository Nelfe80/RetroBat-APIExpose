using System.Diagnostics;
using System.Drawing;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Domain.Services;

namespace RetroBat.Api.Media;

public sealed class MarqueeAutogenService
{
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly EmulationStationSettingsService _settingsService;
    private readonly EsProjectionService _projectionService;
    private readonly IEmulationStationNotificationService _notificationService;
    private readonly InterfaceTextService _interfaceTextService;
    private readonly ILogger<MarqueeAutogenService>? _logger;

    public MarqueeAutogenService(
        ApiExposeRuntimeOptionsService runtimeOptions,
        EmulationStationSettingsService settingsService,
        EsProjectionService projectionService,
        IEmulationStationNotificationService notificationService,
        InterfaceTextService interfaceTextService,
        ILogger<MarqueeAutogenService>? logger = null)
    {
        _runtimeOptions = runtimeOptions;
        _settingsService = settingsService;
        _projectionService = projectionService;
        _notificationService = notificationService;
        _interfaceTextService = interfaceTextService;
        _logger = logger;
    }

    public async Task<MarqueeAutogenResult> GenerateAfterRemoteScrapeAsync(
        MediaProjectionPlan plan,
        RemoteScrapeDecision decision,
        bool isCurrentGame,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!isCurrentGame)
        {
            return MarqueeAutogenResult.Skipped("not-current-game");
        }

        return await GenerateCoreAsync(
            plan,
            decision,
            requireRemoteMarqueeRequest: false,
            cancellationToken);
    }

    public async Task<MarqueeAutogenResult> GenerateForSelectedGameAsync(
        MediaProjectionPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var decision = new RemoteScrapeDecision
        {
            Provider = "local",
            Status = "selection-autogen",
            SystemId = plan.SystemId,
            FrontendSystemId = plan.FrontendSystemId,
            GameSlug = plan.GameSlug,
            GamePath = plan.GamePath,
            MissingKinds = [MediaKinds.Marquee]
        };

        return await GenerateCoreAsync(
            plan,
            decision,
            requireRemoteMarqueeRequest: false,
            cancellationToken);
    }

    private async Task<MarqueeAutogenResult> GenerateCoreAsync(
        MediaProjectionPlan plan,
        RemoteScrapeDecision decision,
        bool requireRemoteMarqueeRequest,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var profile = ResolveProfile(_runtimeOptions.GetMarqueeManagerAutogenProfile());
        if (profile == null)
        {
            return MarqueeAutogenResult.Skipped("disabled");
        }

        if (!_runtimeOptions.IsRemoteMarqueeScrapingEnabled())
        {
            return MarqueeAutogenResult.Skipped("marquee-scraping-disabled");
        }

        if (requireRemoteMarqueeRequest &&
            !decision.MissingKinds.Contains(MediaKinds.Marquee, StringComparer.OrdinalIgnoreCase) &&
            !decision.ImportedKinds.Contains(MediaKinds.Marquee, StringComparer.OrdinalIgnoreCase))
        {
            return MarqueeAutogenResult.Skipped("marquee-not-requested");
        }

        var marqueeNeed = FindNeed(plan, MediaKinds.Marquee);
        if (marqueeNeed == null)
        {
            return MarqueeAutogenResult.Skipped("missing-marquee-need");
        }

        var cleanedFalseMarquee = await CleanupFalseConsoleMarqueeAsync(plan, marqueeNeed, profile, cancellationToken);
        if (HasUsableMarquee(plan, marqueeNeed, profile))
        {
            return MarqueeAutogenResult.Skipped(cleanedFalseMarquee ? "true-marquee-after-cleanup" : "true-marquee-present");
        }

        if (HasUsableGeneratedMarquee(plan, profile))
        {
            return MarqueeAutogenResult.Skipped("generated-marquee-present");
        }

        var fanartPath = ResolveExistingKindPath(plan, MediaKinds.Fanart);
        var logoPath = ResolveLogoPath(plan);
        if (string.IsNullOrWhiteSpace(fanartPath) || !File.Exists(fanartPath))
        {
            await AuditAsync(plan, "skipped-no-fanart", profile, null, null, cancellationToken);
            return MarqueeAutogenResult.Skipped("missing-fanart");
        }

        if (string.IsNullOrWhiteSpace(logoPath) || !File.Exists(logoPath))
        {
            await AuditAsync(plan, "skipped-no-logo", profile, fanartPath, null, cancellationToken);
            return MarqueeAutogenResult.Skipped("missing-logo");
        }

        var convertPath = Path.Combine(RetroBatPaths.ToolsRoot, "imagemagick", "convert.exe");
        if (!File.Exists(convertPath))
        {
            await AuditAsync(plan, "skipped-missing-convert", profile, fanartPath, logoPath, cancellationToken);
            return MarqueeAutogenResult.Skipped("missing-convert");
        }

        var tempDirectory = Path.Combine(RetroBatPaths.RuntimeTempRoot, "marquee-autogen");
        Directory.CreateDirectory(tempDirectory);
        var tempBasePath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + "-base.png");
        var tempGradientPath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + "-gradient.png");
        var tempOutputPath = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + "-marquee.png");

        try
        {
            var layout = AnalyzeLayout(fanartPath, profile);
            await RunConvertAsync(
                convertPath,
                [
                    fanartPath,
                    "-auto-orient",
                    "-resize", $"{profile.Width}x{profile.Height}^",
                    "-gravity", layout.BackgroundGravity,
                    "-extent", $"{profile.Width}x{profile.Height}",
                    "-colorspace", "sRGB",
                    "-type", "TrueColorAlpha",
                    Png32(tempBasePath)
                ],
                cancellationToken);

            var logoMaxWidth = layout.LogoGravity == "Center"
                ? (int)Math.Round(profile.Width * 0.68)
                : (int)Math.Round(profile.Width * 0.42);
            var logoMaxHeight = (int)Math.Round(profile.Height * 0.88);
            var finalBasePath = tempBasePath;
            var gradientPath = ResolveGradientPath(layout.ZoneLuminance);
            if (!string.IsNullOrWhiteSpace(gradientPath) && File.Exists(gradientPath))
            {
                await RunConvertAsync(
                    convertPath,
                    [
                        tempBasePath,
                        gradientPath,
                        "-antialias",
                        "-filter", "Lanczos",
                        "-resize", $"{logoMaxWidth}x{logoMaxHeight}^>",
                        "-gravity", layout.LogoGravity,
                        "-geometry", ResolveGradientGeometry(layout.LogoGravity),
                        "-composite",
                        "-colorspace", "sRGB",
                        "-type", "TrueColorAlpha",
                        Png32(tempGradientPath)
                    ],
                    cancellationToken);
                finalBasePath = tempGradientPath;
            }

            var finalArguments = new List<string> { finalBasePath };
            if (string.IsNullOrWhiteSpace(gradientPath) || !File.Exists(gradientPath))
            {
                var drawRectangle = ResolveLogoBackdropRectangle(profile, layout.LogoGravity);
                var overlayColor = layout.ZoneLuminance >= 145
                    ? "#0000006B"
                    : "#FFFFFF4D";
                finalArguments.AddRange(["-fill", overlayColor, "-draw", drawRectangle]);
            }

            finalArguments.AddRange(
            [
                "-colorspace",
                "sRGB",
                "-type",
                "TrueColorAlpha",
                "(",
                logoPath,
                "-auto-orient",
                "-colorspace",
                "sRGB",
                "-type",
                "TrueColorAlpha",
                "-antialias",
                "-filter", "Lanczos",
                "-resize", $"{logoMaxWidth}x{logoMaxHeight}>",
                ")",
                "-gravity", layout.LogoGravity,
                "-geometry", ResolveLogoGeometry(layout.LogoGravity, profile),
                "-composite",
                "-colorspace", "sRGB",
                "-type", "TrueColorAlpha",
                Png32(tempOutputPath)
            ]);
            await RunConvertAsync(convertPath, finalArguments, cancellationToken);

            var generatedPath = await SaveGeneratedMarqueeAsync(
                plan,
                tempOutputPath,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(generatedPath) || !File.Exists(generatedPath))
            {
                await AuditAsync(plan, "import-failed", profile, fanartPath, logoPath, cancellationToken);
                return MarqueeAutogenResult.Skipped("import-failed");
            }

            decision.ImportedMediaCount++;
            decision.MediaContentChanged = true;
            await AuditAsync(plan, "generated", profile, fanartPath, logoPath, cancellationToken, generatedPath);
            if (_runtimeOptions.ShouldNotifyMarqueeAutogen())
            {
                await _notificationService.NotifyAsync(
                    ResolveMarqueeGeneratedMessage(plan),
                    cancellationToken);
            }

            return MarqueeAutogenResult.Generated(generatedPath, cleanedFalseMarquee);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(
                ex,
                "Marquee autogen failed for system={SystemId}, game={GameSlug}.",
                plan.FrontendSystemId,
                plan.GameSlug);
            await AuditAsync(plan, "failed", profile, fanartPath, logoPath, cancellationToken, error: ex.GetType().Name);
            return MarqueeAutogenResult.Skipped("failed");
        }
        finally
        {
            TryDelete(tempBasePath);
            TryDelete(tempGradientPath);
            TryDelete(tempOutputPath);
        }
    }

    private async Task<bool> CleanupFalseConsoleMarqueeAsync(
        MediaProjectionPlan plan,
        MediaNeed marqueeNeed,
        MarqueeAutogenProfile profile,
        CancellationToken cancellationToken)
    {
        var candidates = ResolveCanonicalMarqueeCandidates(plan, marqueeNeed).ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        var comparisonPaths = ResolveScreenMarqueeComparisonPaths(plan).ToList();
        var generatedDmdPaths = ResolveGeneratedDmdComparisonPaths(plan).ToList();
        var cleaned = false;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(candidate))
            {
                continue;
            }

            var cleanupReason = IsScreenMarqueeFallback(candidate, comparisonPaths)
                ? "screenmarquee-fallback"
                : IsGeneratedDmdFallback(candidate, generatedDmdPaths)
                    ? "generated-dmd-fallback"
                : IsClearlyInvalidMarqueeCandidate(candidate, profile)
                    ? "invalid-marquee-size"
                    : string.Empty;
            if (string.IsNullOrWhiteSpace(cleanupReason))
            {
                continue;
            }

            TryDelete(candidate);
            cleaned = true;
            if (PathsEqual(candidate, marqueeNeed.ExistingPath))
            {
                marqueeNeed.ExistingPath = string.Empty;
            }

            if (PathsEqual(candidate, marqueeNeed.ImportedPath))
            {
                marqueeNeed.ImportedPath = string.Empty;
            }

            marqueeNeed.IsMissing = true;
            await MediaUpdateAuditLog.AppendAsync(
                plan,
                "marquee-autogen-cleanup",
                "marquee",
                "removed-false-console-marquee",
                new { path = candidate, reason = cleanupReason },
                cancellationToken);
        }

        return cleaned;
    }

    private bool HasUsableMarquee(MediaProjectionPlan plan, MediaNeed marqueeNeed, MarqueeAutogenProfile profile)
    {
        var candidates = ResolveCanonicalMarqueeCandidates(plan, marqueeNeed).ToList();
        if (candidates.Count == 0)
        {
            return false;
        }

        return candidates.Any(candidate => IsUsableMarqueeCandidate(candidate, profile));
    }

    private static bool HasUsableGeneratedMarquee(MediaProjectionPlan plan, MarqueeAutogenProfile profile)
    {
        var generatedPath = ResolveGeneratedMarqueePath(plan);
        return IsUsableMarqueeCandidate(generatedPath, profile);
    }

    private static async Task<string> SaveGeneratedMarqueeAsync(
        MediaProjectionPlan plan,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var generatedPath = ResolveGeneratedMarqueePath(plan);
        var directory = Path.GetDirectoryName(generatedPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return string.Empty;
        }

        Directory.CreateDirectory(directory);
        var tempPath = generatedPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            await using (var destination = File.Create(tempPath))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            File.Move(tempPath, generatedPath, overwrite: true);
            return generatedPath;
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static string ResolveGeneratedMarqueePath(MediaProjectionPlan plan)
    {
        return Path.Combine(
            RetroBatPaths.MediaSystemsRoot,
            plan.SystemId,
            "games",
            plan.GameSlug,
            "artwork",
            "marquee",
            "generated-marquee.png");
    }

    private static bool IsUsableMarqueeCandidate(string path, MarqueeAutogenProfile profile)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var image = Image.FromFile(path);
            var minWidth = (int)Math.Round(profile.Width * 0.50);
            var minHeight = (int)Math.Round(profile.Height * 0.50);
            if (image.Width < minWidth || image.Height < minHeight)
            {
                return false;
            }

            var aspect = image.Width / (double)Math.Max(1, image.Height);
            return aspect >= 2.35 && aspect <= 10.0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsClearlyInvalidMarqueeCandidate(string path, MarqueeAutogenProfile profile)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var image = Image.FromFile(path);
            var minWidth = (int)Math.Round(profile.Width * 0.50);
            var minHeight = (int)Math.Round(profile.Height * 0.50);
            return image.Width < minWidth || image.Height < minHeight;
        }
        catch
        {
            return false;
        }
    }

    private IEnumerable<string> ResolveCanonicalMarqueeCandidates(MediaProjectionPlan plan, MediaNeed marqueeNeed)
    {
        foreach (var path in new[] { marqueeNeed.ImportedPath, marqueeNeed.ExistingPath })
        {
            var resolved = ResolvePhysicalPath(plan.FrontendSystemId, path);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                yield return resolved;
            }
        }

        var systemMarqueeDir = Path.Combine(
            RetroBatPaths.MediaSystemsRoot,
            plan.SystemId,
            "games",
            plan.GameSlug,
            "artwork",
            "marquee");
        if (Directory.Exists(systemMarqueeDir))
        {
            foreach (var candidate in Directory.EnumerateFiles(systemMarqueeDir, "marquee.*", SearchOption.TopDirectoryOnly))
            {
                yield return candidate;
            }
        }
    }

    private IEnumerable<string> ResolveScreenMarqueeComparisonPaths(MediaProjectionPlan plan)
    {
        foreach (var kind in new[] { MediaKinds.ScreenMarquee, MediaKinds.ScreenMarqueeSmall })
        {
            var path = ResolveExistingKindPath(plan, kind);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> ResolveGeneratedDmdComparisonPaths(MediaProjectionPlan plan)
    {
        var marqueeDirectory = Path.Combine(
            RetroBatPaths.MediaSystemsRoot,
            plan.SystemId,
            "games",
            plan.GameSlug,
            "artwork",
            "marquee");
        if (!Directory.Exists(marqueeDirectory))
        {
            yield break;
        }

        foreach (var pattern in new[] { "generated-dmd.*", "generated-system-dmd.*" })
        {
            foreach (var path in Directory.EnumerateFiles(marqueeDirectory, pattern, SearchOption.TopDirectoryOnly))
            {
                yield return path;
            }
        }
    }

    private static bool IsScreenMarqueeFallback(string marqueePath, IReadOnlyList<string> comparisonPaths)
    {
        return comparisonPaths.Any(path => FilesHaveSameContent(marqueePath, path));
    }

    private static bool IsGeneratedDmdFallback(string marqueePath, IReadOnlyList<string> comparisonPaths)
    {
        return comparisonPaths.Any(path => FilesHaveSameContent(marqueePath, path));
    }

    private static bool FilesHaveSameContent(string left, string right)
    {
        try
        {
            if (!File.Exists(left) || !File.Exists(right))
            {
                return false;
            }

            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length)
            {
                return false;
            }

            using var leftStream = File.Open(left, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var rightStream = File.Open(right, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> leftBuffer = stackalloc byte[8192];
            Span<byte> rightBuffer = stackalloc byte[8192];
            while (true)
            {
                var leftRead = leftStream.Read(leftBuffer);
                var rightRead = rightStream.Read(rightBuffer);
                if (leftRead != rightRead)
                {
                    return false;
                }

                if (leftRead == 0)
                {
                    return true;
                }

                if (!leftBuffer[..leftRead].SequenceEqual(rightBuffer[..rightRead]))
                {
                    return false;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string left, string? right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
            !string.IsNullOrWhiteSpace(right) &&
            string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveLogoPath(MediaProjectionPlan plan)
    {
        foreach (var kind in new[] { MediaKinds.Wheel, MediaKinds.Logo })
        {
            var path = ResolveExistingKindPath(plan, kind);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }

        return string.Empty;
    }

    private string ResolveExistingKindPath(MediaProjectionPlan plan, string kind)
    {
        var need = FindNeed(plan, kind);
        if (need != null)
        {
            foreach (var rawPath in new[] { need.ImportedPath, need.ExistingPath })
            {
                var resolved = ResolvePhysicalPath(plan.FrontendSystemId, rawPath);
                if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
                {
                    return resolved;
                }
            }
        }

        return _projectionService.ResolveCanonicalSourcePath(plan.SystemId, plan.GameSlug, kind) ?? string.Empty;
    }

    private static string? ResolvePhysicalPath(string frontendSystemId, string? path)
    {
        var normalized = (path ?? string.Empty).Replace('/', Path.DirectorySeparatorChar).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
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

    private static MediaNeed? FindNeed(MediaProjectionPlan plan, string kind)
    {
        return plan.Needs.FirstOrDefault(need =>
            string.Equals(MediaKinds.Normalize(need.Kind), MediaKinds.Normalize(kind), StringComparison.OrdinalIgnoreCase));
    }

    private static MarqueeAutogenProfile? ResolveProfile(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "xl-1920x360" => new MarqueeAutogenProfile("xl-1920x360", 1920, 360),
            "l-1280x400" => new MarqueeAutogenProfile("l-1280x400", 1280, 400),
            "m-920x360" => new MarqueeAutogenProfile("m-920x360", 920, 360),
            _ => null
        };
    }

    private static MarqueeLayout AnalyzeLayout(string fanartPath, MarqueeAutogenProfile profile)
    {
        try
        {
            using var bitmap = new Bitmap(fanartPath);
            var targetAspect = profile.Width / (double)profile.Height;
            var cropHeight = Math.Max(1, Math.Min(bitmap.Height, (int)Math.Round(bitmap.Width / targetAspect)));
            var bands = new[]
            {
                new LayoutBand("North", new Rectangle(0, 0, bitmap.Width, cropHeight)),
                new LayoutBand("Center", new Rectangle(0, Math.Max(0, (bitmap.Height - cropHeight) / 2), bitmap.Width, cropHeight)),
                new LayoutBand("South", new Rectangle(0, Math.Max(0, bitmap.Height - cropHeight), bitmap.Width, cropHeight))
            };

            var selectedBand = bands
                .Select(band => (Band: band, Score: ScoreRichness(bitmap, band.Bounds)))
                .OrderByDescending(entry => entry.Score)
                .First().Band;
            var zoneWidth = Math.Max(1, selectedBand.Bounds.Width / 3);
            var zones = new[]
            {
                new LayoutZone("West", new Rectangle(selectedBand.Bounds.Left, selectedBand.Bounds.Top, zoneWidth, selectedBand.Bounds.Height)),
                new LayoutZone("Center", new Rectangle(selectedBand.Bounds.Left + zoneWidth, selectedBand.Bounds.Top, zoneWidth, selectedBand.Bounds.Height)),
                new LayoutZone("East", new Rectangle(selectedBand.Bounds.Left + zoneWidth * 2, selectedBand.Bounds.Top, selectedBand.Bounds.Width - zoneWidth * 2, selectedBand.Bounds.Height))
            };
            var selectedZone = zones
                .Select(zone => (Zone: zone, Score: ScoreRichness(bitmap, zone.Bounds)))
                .OrderBy(entry => entry.Score)
                .First().Zone;
            var luminance = AverageLuminance(bitmap, selectedZone.Bounds);
            return new MarqueeLayout(selectedBand.Gravity, selectedZone.Gravity, luminance);
        }
        catch
        {
            return new MarqueeLayout("Center", "Center", 128);
        }
    }

    private static double ScoreRichness(Bitmap bitmap, Rectangle bounds)
    {
        var stepX = Math.Max(1, bounds.Width / 96);
        var stepY = Math.Max(1, bounds.Height / 48);
        var count = 0;
        var sum = 0d;
        var sumSquares = 0d;
        var edge = 0d;
        for (var y = bounds.Top; y < bounds.Bottom; y += stepY)
        {
            double? previous = null;
            for (var x = bounds.Left; x < bounds.Right; x += stepX)
            {
                var lum = Luminance(bitmap.GetPixel(Math.Clamp(x, 0, bitmap.Width - 1), Math.Clamp(y, 0, bitmap.Height - 1)));
                sum += lum;
                sumSquares += lum * lum;
                if (previous.HasValue)
                {
                    edge += Math.Abs(lum - previous.Value);
                }

                previous = lum;
                count++;
            }
        }

        if (count == 0)
        {
            return 0;
        }

        var mean = sum / count;
        var variance = Math.Max(0, sumSquares / count - mean * mean);
        return variance + edge / count;
    }

    private static double AverageLuminance(Bitmap bitmap, Rectangle bounds)
    {
        var stepX = Math.Max(1, bounds.Width / 96);
        var stepY = Math.Max(1, bounds.Height / 48);
        var count = 0;
        var sum = 0d;
        for (var y = bounds.Top; y < bounds.Bottom; y += stepY)
        {
            for (var x = bounds.Left; x < bounds.Right; x += stepX)
            {
                sum += Luminance(bitmap.GetPixel(Math.Clamp(x, 0, bitmap.Width - 1), Math.Clamp(y, 0, bitmap.Height - 1)));
                count++;
            }
        }

        return count == 0 ? 128 : sum / count;
    }

    private static double Luminance(Color color)
    {
        return color.R * 0.299 + color.G * 0.587 + color.B * 0.114;
    }

    private static string ResolveLogoBackdropRectangle(MarqueeAutogenProfile profile, string gravity)
    {
        var (x1, x2) = gravity switch
        {
            "West" => (0, profile.Width / 2),
            "East" => (profile.Width / 2, profile.Width),
            _ => (profile.Width / 4, profile.Width * 3 / 4)
        };

        return $"rectangle {x1},0 {x2},{profile.Height}";
    }

    private static string ResolveLogoGeometry(string gravity, MarqueeAutogenProfile profile)
    {
        var horizontalMargin = Math.Max(30, Math.Min(50, profile.Width / 26));
        return gravity switch
        {
            "West" => $"+{horizontalMargin}+0",
            "East" => $"+{horizontalMargin}+0",
            _ => "+0+0"
        };
    }

    private static string ResolveGradientGeometry(string gravity)
    {
        return gravity switch
        {
            "West" or "East" => "+50+0",
            _ => "+0+0"
        };
    }

    private static string ResolveGradientPath(double zoneLuminance)
    {
        var fileName = zoneLuminance >= 145
            ? "gradient_black.png"
            : "gradient_white.png";
        return Path.Combine(RetroBatPaths.ToolsRoot, "imagemagick", fileName);
    }

    private static async Task RunConvertAsync(string convertPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = convertPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("ImageMagick convert.exe could not be started.");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask;
        var stdout = await stdoutTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ImageMagick convert.exe failed with exit code {process.ExitCode}: {stderr}{stdout}");
        }
    }

    private static async Task AuditAsync(
        MediaProjectionPlan plan,
        string status,
        MarqueeAutogenProfile profile,
        string? fanartPath,
        string? logoPath,
        CancellationToken cancellationToken,
        string? outputPath = null,
        string? error = null)
    {
        await MediaUpdateAuditLog.AppendAsync(
            plan,
            "marquee-autogen",
            "marquee",
            status,
            new
            {
                profile = profile.Name,
                profile.Width,
                profile.Height,
                fanartPath,
                logoPath,
                outputPath,
                error
            },
            cancellationToken);
    }

    private static string ResolveDisplayName(MediaProjectionPlan plan)
    {
        return string.IsNullOrWhiteSpace(plan.DisplayName) ? plan.GameSlug : plan.DisplayName;
    }

    private string ResolveMarqueeGeneratedMessage(MediaProjectionPlan plan)
    {
        return _interfaceTextService.Format(
            "notification.marquee.generated",
            _settingsService.GetScrapingSettings().Language,
            ("game", ResolveDisplayName(plan)));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Cleanup best effort only.
        }
    }

    private static string Png32(string path) => "png32:" + path;

    private sealed record MarqueeAutogenProfile(string Name, int Width, int Height);
    private sealed record MarqueeLayout(string BackgroundGravity, string LogoGravity, double ZoneLuminance);
    private sealed record LayoutBand(string Gravity, Rectangle Bounds);
    private sealed record LayoutZone(string Gravity, Rectangle Bounds);
}

public sealed record MarqueeAutogenResult(bool WasGenerated, string Status, string ImportedPath, bool CleanedFalseMarquee)
{
    public string GeneratedPath => ImportedPath;

    public static MarqueeAutogenResult Generated(string importedPath, bool cleanedFalseMarquee)
    {
        return new MarqueeAutogenResult(true, "generated", importedPath, cleanedFalseMarquee);
    }

    public static MarqueeAutogenResult Skipped(string status)
    {
        return new MarqueeAutogenResult(false, status, string.Empty, false);
    }
}
