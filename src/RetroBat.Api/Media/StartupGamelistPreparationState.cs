using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

public sealed class StartupGamelistPreparationState
{
    public Dictionary<string, Dictionary<string, StartupGamelistPhaseState>> Systems { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class StartupGamelistPhaseState
{
    public int StateVersion { get; set; }
    public long GamelistWriteTicksUtc { get; set; }
    public long GamelistByteLength { get; set; }
    public string GamelistFingerprint { get; set; } = string.Empty;
    public string SettingsSignature { get; set; } = string.Empty;
    public string MediaFingerprint { get; set; } = string.Empty;
    public string PackFingerprint { get; set; } = string.Empty;
    public string NormalizerVersion { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed record StartupGamelistPhaseCacheStatus(bool IsClean, string Reason);

public static class StartupGamelistPreparationStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static StartupGamelistPreparationState Load()
    {
        var path = RetroBatPaths.StartupGamelistPreparationStatePath;
        try
        {
            if (!File.Exists(path))
            {
                return new StartupGamelistPreparationState();
            }

            using var stream = File.OpenRead(path);
            var state = JsonSerializer.Deserialize<StartupGamelistPreparationState>(stream, JsonOptions)
                ?? new StartupGamelistPreparationState();
            var loadedSystems = state.Systems ?? new Dictionary<string, Dictionary<string, StartupGamelistPhaseState>>();
            state.Systems = new Dictionary<string, Dictionary<string, StartupGamelistPhaseState>>(
                loadedSystems.ToDictionary(
                    pair => pair.Key,
                    pair => new Dictionary<string, StartupGamelistPhaseState>(
                        pair.Value ?? new Dictionary<string, StartupGamelistPhaseState>(),
                        StringComparer.OrdinalIgnoreCase),
                    StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch
        {
            return new StartupGamelistPreparationState();
        }
    }

    public static void Save(StartupGamelistPreparationState state)
    {
        try
        {
            var path = RetroBatPaths.StartupGamelistPreparationStatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? RetroBatPaths.MediaAliasesSharedRoot);
            using var stream = File.Create(path);
            JsonSerializer.Serialize(stream, state, JsonOptions);
        }
        catch
        {
            // Startup cache must never block the real gamelist preparation work.
        }
    }

    public static bool IsSystemPhaseClean(
        StartupGamelistPreparationState state,
        string systemId,
        string phase,
        string gamelistPath,
        int stateVersion,
        string normalizerVersion,
        string settingsSignature = "",
        string mediaFingerprint = "",
        string packFingerprint = "")
    {
        return GetSystemPhaseCacheStatus(
                state,
                systemId,
                phase,
                gamelistPath,
                stateVersion,
                normalizerVersion,
                settingsSignature,
                mediaFingerprint,
                packFingerprint)
            .IsClean;
    }

    public static StartupGamelistPhaseCacheStatus GetSystemPhaseCacheStatus(
        StartupGamelistPreparationState state,
        string systemId,
        string phase,
        string gamelistPath,
        int stateVersion,
        string normalizerVersion,
        string settingsSignature = "",
        string mediaFingerprint = "",
        string packFingerprint = "")
    {
        if (!File.Exists(gamelistPath))
        {
            return new StartupGamelistPhaseCacheStatus(false, "missing-gamelist");
        }

        if (!state.Systems.TryGetValue(systemId, out var phases))
        {
            return new StartupGamelistPhaseCacheStatus(false, "no-system-cache");
        }

        if (!phases.TryGetValue(phase, out var cached))
        {
            return new StartupGamelistPhaseCacheStatus(false, "no-phase-cache");
        }

        if (cached.StateVersion != stateVersion)
        {
            return new StartupGamelistPhaseCacheStatus(false, "state-version");
        }

        if (!string.Equals(cached.NormalizerVersion, normalizerVersion, StringComparison.Ordinal))
        {
            return new StartupGamelistPhaseCacheStatus(false, "normalizer-version");
        }

        if (!string.Equals(cached.SettingsSignature, settingsSignature, StringComparison.Ordinal))
        {
            return new StartupGamelistPhaseCacheStatus(false, "settings-signature");
        }

        if (!string.Equals(cached.MediaFingerprint, mediaFingerprint, StringComparison.Ordinal))
        {
            return new StartupGamelistPhaseCacheStatus(false, "media-fingerprint");
        }

        if (!string.Equals(cached.PackFingerprint, packFingerprint, StringComparison.Ordinal))
        {
            return new StartupGamelistPhaseCacheStatus(false, "pack-fingerprint");
        }

        var fileInfo = new FileInfo(gamelistPath);
        if (cached.GamelistWriteTicksUtc == fileInfo.LastWriteTimeUtc.Ticks &&
            cached.GamelistByteLength == fileInfo.Length)
        {
            return new StartupGamelistPhaseCacheStatus(true, "clean-file-stamp");
        }

        return string.Equals(cached.GamelistFingerprint, ComputeFileHash(gamelistPath), StringComparison.OrdinalIgnoreCase)
            ? new StartupGamelistPhaseCacheStatus(true, "clean-file-hash")
            : new StartupGamelistPhaseCacheStatus(false, "gamelist-content");
    }

    public static void MarkSystemPhaseClean(
        StartupGamelistPreparationState state,
        string systemId,
        string phase,
        string gamelistPath,
        int stateVersion,
        string normalizerVersion,
        string settingsSignature = "",
        string mediaFingerprint = "",
        string packFingerprint = "")
    {
        if (!File.Exists(gamelistPath))
        {
            return;
        }

        if (!state.Systems.TryGetValue(systemId, out var phases))
        {
            phases = new Dictionary<string, StartupGamelistPhaseState>(StringComparer.OrdinalIgnoreCase);
            state.Systems[systemId] = phases;
        }

        var fileInfo = new FileInfo(gamelistPath);
        phases[phase] = new StartupGamelistPhaseState
        {
            StateVersion = stateVersion,
            GamelistWriteTicksUtc = fileInfo.LastWriteTimeUtc.Ticks,
            GamelistByteLength = fileInfo.Length,
            GamelistFingerprint = ComputeFileHash(gamelistPath),
            SettingsSignature = settingsSignature,
            MediaFingerprint = mediaFingerprint,
            PackFingerprint = packFingerprint,
            NormalizerVersion = normalizerVersion,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static string ComputeFileHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return string.Empty;
        }
    }
}

public static class StartupGamelistPreparationLog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task AppendAsync(string phase, string status, object details, CancellationToken cancellationToken)
    {
        try
        {
            var logPath = Path.Combine(RetroBatPaths.PluginRoot, ".log", "startup-gamelist-preparation.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? RetroBatPaths.PluginRoot);
            var line = JsonSerializer.Serialize(
                new
                {
                    at = DateTimeOffset.Now,
                    phase,
                    status,
                    details
                },
                JsonOptions);
            await File.AppendAllTextAsync(logPath, line + Environment.NewLine, Encoding.UTF8, cancellationToken);
        }
        catch
        {
            // Startup diagnostics must never block the real gamelist preparation work.
        }
    }
}

public static class StartupGamelistPreparationDiagnostics
{
    public static object BuildStatus(int recentLogEntries)
    {
        var state = StartupGamelistPreparationStateStore.Load();
        var logPath = Path.Combine(RetroBatPaths.PluginRoot, ".log", "startup-gamelist-preparation.jsonl");
        var phaseSummaries = state.Systems
            .SelectMany(system => system.Value.Select(phase => new { SystemId = system.Key, Phase = phase.Key, State = phase.Value }))
            .GroupBy(entry => entry.Phase, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                phase = group.Key,
                systems = group.Count(),
                lastUpdatedAtUtc = group
                    .Select(entry => entry.State.UpdatedAtUtc)
                    .DefaultIfEmpty()
                    .Max(),
                normalizerVersions = group
                    .Select(entry => entry.State.NormalizerVersion)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray()
            })
            .ToArray();

        return new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            state = new
            {
                path = RetroBatPaths.StartupGamelistPreparationStatePath,
                exists = File.Exists(RetroBatPaths.StartupGamelistPreparationStatePath),
                systems = state.Systems.Count,
                phaseCount = state.Systems.Sum(system => system.Value.Count),
                phases = phaseSummaries
            },
            log = new
            {
                path = logPath,
                exists = File.Exists(logPath),
                recent = ReadRecentLogEntries(logPath, Math.Clamp(recentLogEntries, 0, 200))
            },
            triggers = new
            {
                esSettings = DescribeFile(RetroBatPaths.EmulationStationSettingsPath),
                appsettings = DescribeFile(Path.Combine(RetroBatPaths.PluginRoot, "appsettings.json")),
                romsRoot = DescribeRomsRoot(RetroBatPaths.RomsRoot),
                romPackRoot = DescribeTopLevelDirectory(Path.Combine(RetroBatPaths.PluginRoot, "package-installer")),
                collectionPackRoot = DescribeTopLevelDirectory(Path.Combine(RetroBatPaths.PluginRoot, "package-installer", "collections")),
                romPackStartupState = DescribeFile(RetroBatPaths.RomPackInstallerStartupStatePath),
                romPackIndex = DescribeFile(RetroBatPaths.RomPackInstallerIndexPath),
                collectionPackIndex = DescribeFile(RetroBatPaths.CollectionPackInstallerIndexPath),
                pendingExtendedGamelists = DescribeTopLevelDirectory(Path.Combine(RetroBatPaths.MediaAliasesSharedRoot, "gamelist-extended-pending"))
            }
        };
    }

    private static object DescribeFile(string path)
    {
        if (!File.Exists(path))
        {
            return new
            {
                path,
                exists = false
            };
        }

        var info = new FileInfo(path);
        return new
        {
            path,
            exists = true,
            bytes = info.Length,
            lastWriteTimeUtc = info.LastWriteTimeUtc
        };
    }

    private static object DescribeTopLevelDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return new
            {
                path,
                exists = false
            };
        }

        var files = Directory.EnumerateFiles(path, "*.*", SearchOption.TopDirectoryOnly)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Select(file => new FileInfo(file))
            .ToArray();

        return new
        {
            path,
            exists = true,
            files = files.Length,
            bytes = files.Sum(file => file.Length),
            lastWriteTimeUtc = files.Select(file => file.LastWriteTimeUtc).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(path)).Max(),
            fingerprint = ComputeListingFingerprint(path, files)
        };
    }

    private static object DescribeRomsRoot(string path)
    {
        if (!Directory.Exists(path))
        {
            return new
            {
                path,
                exists = false
            };
        }

        var gamelists = Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly)
            .Select(systemDirectory => Path.Combine(systemDirectory, "gamelist.xml"))
            .Where(File.Exists)
            .Select(gamelistPath => new FileInfo(gamelistPath))
            .ToArray();

        return new
        {
            path,
            exists = true,
            systems = Directory.EnumerateDirectories(path, "*", SearchOption.TopDirectoryOnly).Count(),
            gamelists = gamelists.Length,
            gamelistBytes = gamelists.Sum(file => file.Length),
            lastGamelistWriteTimeUtc = gamelists.Select(file => file.LastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max()
        };
    }

    private static string ComputeListingFingerprint(string root, IReadOnlyList<FileInfo> files)
    {
        var builder = new StringBuilder();
        foreach (var file in files)
        {
            builder.Append(Path.GetRelativePath(root, file.FullName).Replace('\\', '/'))
                .Append('|')
                .Append(file.Length)
                .Append('|')
                .Append(file.LastWriteTimeUtc.Ticks)
                .AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static IReadOnlyList<object> ReadRecentLogEntries(string logPath, int recentLogEntries)
    {
        if (recentLogEntries <= 0 || !File.Exists(logPath))
        {
            return Array.Empty<object>();
        }

        try
        {
            return File.ReadLines(logPath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .TakeLast(recentLogEntries)
                .Select(ParseLogLine)
                .ToArray();
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

    private static object ParseLogLine(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return new
            {
                at = root.TryGetProperty("at", out var at) ? at.GetString() : null,
                phase = root.TryGetProperty("phase", out var phase) ? phase.GetString() : null,
                status = root.TryGetProperty("status", out var status) ? status.GetString() : null,
                detailsJson = root.TryGetProperty("details", out var details) ? details.GetRawText() : "{}"
            };
        }
        catch
        {
            return new
            {
                raw = line
            };
        }
    }
}
