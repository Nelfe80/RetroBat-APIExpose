using System.Globalization;
using System.Text.RegularExpressions;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Providers.Hi2Txt;

public class Hi2TxtExtractionService : IHiscoreService
{
    public Task<HiscoreExtractionResult> ExtractAsync(GameReference targetGame, CancellationToken cancellationToken = default)
    {
        var result = new HiscoreExtractionResult
        {
            QueryId = targetGame.GameId,
            QueryMd5 = targetGame.Details?.Md5 ?? string.Empty,
            RomPath = targetGame.GamePath,
            System = targetGame.SystemId,
            Game = targetGame.GameName,
            UpdatedAt = DateTime.Now
        };

        var romName = Path.GetFileNameWithoutExtension(targetGame.GamePath);
        result.RomName = romName;

        var candidates = EnumerateSaveFiles(targetGame, romName).ToList();
        if (candidates.Count == 0)
        {
            result.Status = "not_found";
            result.Message = "No hiscore file (nvram, saveram, eeprom, earom, game, .hi) found for this game.";
            return Task.FromResult(result);
        }

        var descriptorPath = ResolveDescriptorPath(romName);
        if (string.IsNullOrWhiteSpace(descriptorPath) || !File.Exists(descriptorPath))
        {
            result.Status = "not_found";
            result.Message = $"No hi2txt XML descriptor found for ROM '{romName}'.";
            return Task.FromResult(result);
        }

        Exception? lastException = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var scores = PostProcessKnownEdgeCases(
                    romName,
                    Hi2TxtXmlScoreParser.ParseScores(candidate.Path, descriptorPath).ToList());
                if (scores.Count > 0 || string.IsNullOrEmpty(result.SourceFile))
                {
                    result.SourceType = candidate.SourceType;
                    result.SourceFile = candidate.Path;
                    result.Scores = scores;
                }

                if (scores.Count > 0)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        if (lastException != null && result.Scores.Count == 0)
        {
            result.Status = "error";
            result.Message = $"Internal hi2txt XML parser failed: {lastException.Message}";
            return Task.FromResult(result);
        }

        result.Status = "ok";
        result.Message = result.Scores.Count > 0
            ? "hiscore extracted"
            : "hiscore extracted but no scores parsed";
        result.UpdatedAt = DateTime.Now;

        return Task.FromResult(result);
    }

    private static List<HiscoreEntry> PostProcessKnownEdgeCases(string romName, List<HiscoreEntry> scores)
    {
        if (scores.Count == 0)
        {
            return scores;
        }

        if (string.Equals(romName, "tigeroad", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(romName, "pang3", StringComparison.OrdinalIgnoreCase))
        {
            return scores
                .OrderByDescending(static entry => TryParseNumericScore(entry.Score, out var score) ? score : long.MinValue)
                .ThenBy(static entry => entry.Rank, StringComparer.OrdinalIgnoreCase)
                .Select((entry, index) => new HiscoreEntry
                {
                    Rank = (index + 1).ToString(CultureInfo.InvariantCulture),
                    Score = entry.Score,
                    Name = entry.Name
                })
                .ToList();
        }

        if (string.Equals(romName, "paradise", StringComparison.OrdinalIgnoreCase))
        {
            return scores
                .Select(static entry => new HiscoreEntry
                {
                    Rank = entry.Rank,
                    Score = TrimNumericLeadingZeros(entry.Score),
                    Name = string.Empty
                })
                .ToList();
        }

        return scores;
    }

    private static bool TryParseNumericScore(string? text, out long value)
    {
        var cleaned = Regex.Replace(text ?? string.Empty, @"[^0-9\-]", string.Empty);
        return long.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static string TrimNumericLeadingZeros(string? text)
    {
        var normalized = text ?? string.Empty;
        var trimmed = normalized.TrimStart('0');
        return string.IsNullOrEmpty(trimmed) ? "0" : trimmed;
    }

    private static IEnumerable<(string Path, string SourceType)> EnumerateSaveFiles(GameReference targetGame, string romName)
    {
        string[] types = { "nvram", "saveram", "eeprom", "earom", "game" };
        var isMameFamily = IsMameFamilyTarget(targetGame);
        string[] searchSystems = (isMameFamily ? new[] { targetGame.SystemId, "mame" } : new[] { targetGame.SystemId })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var homeHiRoots = isMameFamily ? ResolveHomeHiRoots(targetGame) : Array.Empty<string>();

        foreach (var sys in searchSystems)
        {
            foreach (var nvramDir in EnumerateNvramDirectories(sys, romName))
            {
                foreach (var type in types)
                {
                    var file = Path.Combine(nvramDir, type);
                    if (File.Exists(file))
                    {
                        yield return (file, type);
                    }
                }
            }
        }

        if (!isMameFamily)
        {
            yield break;
        }

        foreach (var sys in searchSystems)
        {
            foreach (var hiFile in EnumerateHiFiles(sys, romName, homeHiRoots))
            {
                if (File.Exists(hiFile))
                {
                    yield return (hiFile, "hi");
                }
            }
        }
    }

    private static bool IsMameFamilyTarget(GameReference targetGame)
    {
        var systemId = (targetGame.SystemId ?? string.Empty).Trim();
        if (systemId.Equals("mame", StringComparison.OrdinalIgnoreCase) ||
            systemId.Equals("fbneo", StringComparison.OrdinalIgnoreCase) ||
            systemId.Equals("fba", StringComparison.OrdinalIgnoreCase) ||
            systemId.Equals("hbmame", StringComparison.OrdinalIgnoreCase) ||
            systemId.Equals("arcade", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedCore = NormalizeCoreKey(targetGame.Launch);
        return normalizedCore.StartsWith("mame", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateNvramDirectories(string systemId, string romName)
    {
        var systemRoot = Path.Combine(RetroBatPaths.SavesRoot, systemId);
        yield return Path.Combine(systemRoot, "nvram", romName);

        if (!Directory.Exists(systemRoot))
        {
            yield break;
        }

        foreach (var childDir in Directory.EnumerateDirectories(systemRoot))
        {
            yield return Path.Combine(childDir, "nvram", romName);
        }
    }

    private static IEnumerable<string> EnumerateHiFiles(string systemId, string romName, IReadOnlyList<string> homeHiRoots)
    {
        var systemRoot = Path.Combine(RetroBatPaths.SavesRoot, systemId);
        yield return Path.Combine(systemRoot, "hi", romName + ".hi");

        foreach (var homeHiRoot in homeHiRoots)
        {
            yield return Path.Combine(homeHiRoot, romName + ".hi");
        }

        if (!Directory.Exists(systemRoot))
        {
            yield break;
        }

        foreach (var childDir in Directory.EnumerateDirectories(systemRoot))
        {
            yield return Path.Combine(childDir, "hi", romName + ".hi");
        }
    }

    private static string ResolveDescriptorPath(string romName)
    {
        var rootPath = ResolveDescriptorCandidatePath(romName);
        if (string.IsNullOrWhiteSpace(rootPath) || !File.Exists(rootPath))
        {
            return string.Empty;
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentPath = rootPath;

        while (File.Exists(currentPath) && visited.Add(currentPath))
        {
            var content = File.ReadAllText(currentPath);
            var sameAs = Regex.Match(
                content,
                "<sameas\\s+id=\"(?<id>[^\"]+)\"\\s*/?>",
                RegexOptions.IgnoreCase).Groups["id"].Value;
            if (string.IsNullOrWhiteSpace(sameAs))
            {
                return currentPath;
            }

            currentPath = Path.Combine(Path.GetDirectoryName(currentPath)!, sameAs + ".xml");
        }

        return currentPath;
    }

    private static string ResolveDescriptorCandidatePath(string romName)
    {
        var fileName = romName + ".xml";
        var descriptorPath = Path.Combine(RetroBatPaths.ThemeHiscoreParsingDbRoot, fileName);
        return File.Exists(descriptorPath) ? descriptorPath : string.Empty;
    }

    private static IReadOnlyList<string> ResolveHomeHiRoots(GameReference targetGame)
    {
        var roots = new List<string>();

        foreach (var homeRoot in ResolveHomeRoots(targetGame))
        {
            roots.Add(Path.Combine(homeRoot, "hiscore"));
        }

        foreach (var folderName in ResolveMameFamilyFolders(targetGame))
        {
            roots.Add(Path.Combine(RetroBatPaths.RetroBatRoot, "bios", folderName, "hiscore"));
        }

        if (!roots.Any())
        {
            roots.Add(RetroBatPaths.BiosMameHiscoreRoot);
        }

        return roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ResolveHomeRoots(GameReference targetGame)
    {
        var roots = new List<string>();

        foreach (var command in EnumerateLaunchCommands(targetGame.Launch))
        {
            var homePath = ExtractArgumentValue(command, "-homepath");
            if (!string.IsNullOrWhiteSpace(homePath))
            {
                roots.AddRange(SplitPathList(homePath));
            }
        }

        var normalizedCore = NormalizeCoreKey(targetGame.Launch);
        if (IsStandaloneMame(normalizedCore))
        {
            roots.AddRange(ReadConfiguredHomePaths(RetroBatPaths.MameIniPath));
        }

        foreach (var familyFolder in ResolveMameFamilyFolders(targetGame))
        {
            roots.Add(Path.Combine(RetroBatPaths.RetroBatRoot, "bios", familyFolder));
        }

        return roots
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ResolveMameFamilyFolders(GameReference targetGame)
    {
        var normalizedCore = NormalizeCoreKey(targetGame.Launch);
        if (string.IsNullOrWhiteSpace(normalizedCore))
        {
            return new[] { "mame" };
        }

        return normalizedCore switch
        {
            "mame2003plus" => new[] { "mame2003-plus", "mame2003" },
            "mame2003midway" => new[] { "mame2003" },
            "mame2003" => new[] { "mame2003" },
            "mame2010" => new[] { "mame2010" },
            "mame2014" => new[] { "mame2014" },
            "mame2016" => new[] { "mame2016" },
            "mame2000" => new[] { "mame2000", "mame" },
            "mame" => new[] { "mame" },
            _ => new[] { "mame" }
        };
    }

    private static string NormalizeCoreKey(LaunchDetails? launch)
    {
        if (launch == null)
        {
            return string.Empty;
        }

        var candidates = new[]
        {
            ExtractLibretroDllCore(launch.RunningCommand),
            launch.Core,
            launch.Emulator
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var normalized = candidate
                .Replace("_libretro.dll", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("_libretro", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace(".dll", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("_", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim()
                .ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> EnumerateLaunchCommands(LaunchDetails? launch)
    {
        if (launch == null)
        {
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(launch.RunningCommand))
        {
            yield return launch.RunningCommand;
        }

        if (!string.IsNullOrWhiteSpace(launch.StartupCommand))
        {
            yield return launch.StartupCommand;
        }
    }

    private static string ExtractArgumentValue(string commandLine, string argumentName)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return string.Empty;
        }

        var pattern = $@"{Regex.Escape(argumentName)}\s+(?:""(?<value>[^""]*)""|(?<value>\S+))";
        var match = Regex.Match(commandLine, pattern, RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : string.Empty;
    }

    private static IReadOnlyList<string> SplitPathList(string value)
    {
        return value
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static IReadOnlyList<string> ReadConfiguredHomePaths(string iniPath)
    {
        if (!File.Exists(iniPath))
        {
            return Array.Empty<string>();
        }

        var line = File.ReadLines(iniPath)
            .Select(static l => l.Trim())
            .FirstOrDefault(static l => l.StartsWith("homepath", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(line))
        {
            return Array.Empty<string>();
        }

        var parts = Regex.Split(line, @"\s+")
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        if (parts.Length < 2)
        {
            return Array.Empty<string>();
        }

        return SplitPathList(parts[^1]);
    }

    private static bool IsStandaloneMame(string normalizedCore)
    {
        return string.IsNullOrWhiteSpace(normalizedCore)
            || normalizedCore.Equals("mame", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractLibretroDllCore(string runningCommand)
    {
        if (string.IsNullOrWhiteSpace(runningCommand))
        {
            return string.Empty;
        }

        var match = Regex.Match(
            runningCommand,
            @"([A-Za-z0-9_\-]+)_libretro\.dll",
            RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}
