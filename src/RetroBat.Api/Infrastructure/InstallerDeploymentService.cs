using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public class InstallerDeploymentService
{
    private static readonly JsonSerializerOptions LogJsonOptions = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { WriteIndented = true };
    private readonly IOptions<ApiExposeOptions> _options;
    private readonly ILogger<InstallerDeploymentService> _logger;

    public InstallerDeploymentService(
        IOptions<ApiExposeOptions> options,
        ILogger<InstallerDeploymentService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task<InstallerDeploymentResult> AuditAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteAsync("audit", dryRun: true, writeLog: false, cancellationToken);
    }

    public Task<InstallerDeploymentResult> DeployAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync("deploy", dryRun, writeLog: true, cancellationToken);
    }

    private async Task<InstallerDeploymentResult> ExecuteAsync(
        string action,
        bool dryRun,
        bool writeLog,
        CancellationToken cancellationToken)
    {
        var options = _options.Value.InstallerDeployment;
        var installerRoot = ResolvePluginPath(options.InstallerRootPath);
        var themesSource = Path.Combine(installerRoot, "themes");
        var scriptsSource = Path.Combine(installerRoot, "scripts");
        var configuredGameInfosSource = ResolvePluginPath(options.GameInfosSourcePath);
        var packagedGameInfosSource = Path.Combine(themesSource, ".gameinfos");
        var gameInfosSource = string.Equals(options.GameInfosSourcePath, "resources/theme/gameinfos", StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(packagedGameInfosSource)
                ? packagedGameInfosSource
                : configuredGameInfosSource;
        var gameInfosTarget = string.IsNullOrWhiteSpace(options.GameInfosTargetPath)
            ? RetroBatPaths.EmulationStationGameInfosThemeRoot
            : ResolvePluginPath(options.GameInfosTargetPath);
        var logPath = ResolvePluginPath(options.LogFilePath);
        var manifestPath = ResolvePluginPath(options.HashManifestPath);
        var manifest = InstallerDeploymentHashManifest.Load(manifestPath);

        var result = new InstallerDeploymentResult
        {
            Action = action,
            Enabled = options.Enabled,
            DryRun = dryRun,
            InstallerRootPath = installerRoot,
            ThemesSourcePath = themesSource,
            ScriptsSourcePath = scriptsSource,
            GameInfosSourcePath = gameInfosSource,
            ThemesTargetPath = RetroBatPaths.EmulationStationThemesRoot,
            ScriptsTargetPath = RetroBatPaths.EmulationStationScriptsRoot,
            GameInfosTargetPath = gameInfosTarget,
            HashManifestPath = manifestPath
        };

        if (!options.Enabled && action.Equals("deploy", StringComparison.OrdinalIgnoreCase))
        {
            result.Warnings.Add("Installer deployment is disabled in appsettings.");
            await WriteLogAsync(logPath, result, writeLog, cancellationToken);
            return result;
        }

        if (!Directory.Exists(installerRoot))
        {
            result.Warnings.Add($"Installer root not found: {installerRoot}");
            await WriteLogAsync(logPath, result, writeLog, cancellationToken);
            return result;
        }

        if (options.SyncThemesOnStartup)
        {
            AddThemeFolder(result, Path.Combine(themesSource, ".medias"), Path.Combine(RetroBatPaths.EmulationStationThemesRoot, ".medias"), overwrite: options.OverwriteMediasFiles, manifest);
        }

        if (options.SyncScriptsOnStartup)
        {
            AddScripts(result, scriptsSource, RetroBatPaths.EmulationStationScriptsRoot, overwrite: options.OverwriteScriptFiles, manifest);
        }

        if (options.SyncGameInfosOnStartup)
        {
            AddGameInfos(result, gameInfosSource, gameInfosTarget, overwrite: options.OverwriteGameInfosFiles, manifest);
        }

        result.CheckedFiles = result.Items.Count;
        result.MissingFiles = result.Items.Count(item => !item.TargetExists);
        result.ChangedFiles = result.Items.Count(item => item.TargetExists && item.NeedsCopy);

        if (action.Equals("deploy", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var item in result.Items.Where(item => item.NeedsCopy))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!dryRun)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
                    File.Copy(item.SourcePath, item.TargetPath, overwrite: true);
                    item.Applied = true;
                    result.CopiedFiles++;
                    manifest.Refresh(item);
                }
            }
        }

        if (action.Equals("deploy", StringComparison.OrdinalIgnoreCase) && !dryRun)
        {
            manifest.Save(manifestPath);
        }

        await WriteLogAsync(logPath, result, writeLog, cancellationToken);
        return result;
    }

    private static void AddThemeFolder(InstallerDeploymentResult result, string sourceRoot, string targetRoot, bool overwrite, InstallerDeploymentHashManifest manifest)
    {
        if (!Directory.Exists(sourceRoot))
        {
            result.Warnings.Add($"Installer theme folder not found: {sourceRoot}");
            return;
        }

        AddDirectoryFiles(result, "theme", sourceRoot, targetRoot, overwrite, manifest);
    }

    private static void AddScripts(InstallerDeploymentResult result, string sourceRoot, string targetRoot, bool overwrite, InstallerDeploymentHashManifest manifest)
    {
        if (!Directory.Exists(sourceRoot))
        {
            result.Warnings.Add($"Installer scripts folder not found: {sourceRoot}");
            return;
        }

        AddDirectoryFiles(result, "script", sourceRoot, targetRoot, overwrite, manifest);
    }

    private static void AddGameInfos(InstallerDeploymentResult result, string sourceRoot, string targetRoot, bool overwrite, InstallerDeploymentHashManifest manifest)
    {
        if (!Directory.Exists(sourceRoot))
        {
            result.Warnings.Add($"Installer gameinfos folder not found: {sourceRoot}");
            return;
        }

        AddDirectoryFiles(result, "gameinfos", sourceRoot, targetRoot, overwrite, manifest);
    }

    private static void AddDirectoryFiles(
        InstallerDeploymentResult result,
        string kind,
        string sourceRoot,
        string targetRoot,
        bool overwrite,
        InstallerDeploymentHashManifest manifest)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            var targetPath = Path.Combine(targetRoot, relativePath);
            var sourceHash = manifest.GetSourceHash(kind, relativePath, sourceFile, targetPath);
            var targetExists = File.Exists(targetPath);
            var differs = targetExists && overwrite && manifest.TargetDiffers(kind, relativePath, sourceFile, targetPath, sourceHash);
            var needsCopy = !targetExists || differs;

            var item = new InstallerDeploymentItem
            {
                Kind = kind,
                RelativePath = relativePath,
                SourcePath = sourceFile,
                TargetPath = targetPath,
                TargetExists = targetExists,
                OverwriteAllowed = overwrite,
                NeedsCopy = needsCopy,
                SourceHash = sourceHash,
                Reason = !targetExists
                    ? "missing"
                    : differs
                        ? "different"
                        : overwrite ? "up-to-date" : "exists-preserved"
            };

            result.Items.Add(item);

            if (targetExists && !needsCopy && overwrite)
            {
                manifest.Refresh(item);
            }
        }
    }

    private static string ResolvePluginPath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return RetroBatPaths.PluginRoot;
        }

        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(RetroBatPaths.PluginRoot, configuredPath));
    }

    private sealed class InstallerDeploymentHashManifest
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, InstallerDeploymentHashEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static InstallerDeploymentHashManifest Load(string path)
        {
            if (!File.Exists(path))
            {
                return new InstallerDeploymentHashManifest();
            }

            try
            {
                return JsonSerializer.Deserialize<InstallerDeploymentHashManifest>(File.ReadAllText(path), ManifestJsonOptions)
                    ?? new InstallerDeploymentHashManifest();
            }
            catch
            {
                return new InstallerDeploymentHashManifest();
            }
        }

        public string GetSourceHash(string kind, string relativePath, string sourcePath, string targetPath)
        {
            var source = new FileInfo(sourcePath);
            var key = BuildKey(kind, relativePath, targetPath);
            if (Files.TryGetValue(key, out var entry) &&
                entry.SourceLength == source.Length &&
                entry.SourceLastWriteTimeUtcTicks == source.LastWriteTimeUtc.Ticks &&
                !string.IsNullOrWhiteSpace(entry.SourceHash))
            {
                return entry.SourceHash;
            }

            return ComputeHash(sourcePath);
        }

        public bool TargetDiffers(string kind, string relativePath, string sourcePath, string targetPath, string sourceHash)
        {
            var source = new FileInfo(sourcePath);
            var target = new FileInfo(targetPath);
            if (target.Length != source.Length)
            {
                return true;
            }

            var key = BuildKey(kind, relativePath, targetPath);
            if (Files.TryGetValue(key, out var entry) &&
                entry.TargetLength == target.Length &&
                entry.TargetLastWriteTimeUtcTicks == target.LastWriteTimeUtc.Ticks &&
                string.Equals(entry.TargetHash, sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !string.Equals(ComputeHash(targetPath), sourceHash, StringComparison.OrdinalIgnoreCase);
        }

        public void Refresh(InstallerDeploymentItem item)
        {
            if (!File.Exists(item.SourcePath) || !File.Exists(item.TargetPath))
            {
                return;
            }

            var source = new FileInfo(item.SourcePath);
            var target = new FileInfo(item.TargetPath);
            var relativePath = string.IsNullOrWhiteSpace(item.RelativePath)
                ? Path.GetFileName(item.TargetPath)
                : item.RelativePath;

            var sourceHash = string.IsNullOrWhiteSpace(item.SourceHash)
                ? ComputeHash(item.SourcePath)
                : item.SourceHash;
            var targetHash = target.Length == source.Length && string.Equals(sourceHash, item.SourceHash, StringComparison.OrdinalIgnoreCase)
                ? sourceHash
                : ComputeHash(item.TargetPath);

            Files[BuildKey(item.Kind, relativePath, item.TargetPath)] = new InstallerDeploymentHashEntry
            {
                SourceLength = source.Length,
                SourceLastWriteTimeUtcTicks = source.LastWriteTimeUtc.Ticks,
                SourceHash = sourceHash,
                TargetLength = target.Length,
                TargetLastWriteTimeUtcTicks = target.LastWriteTimeUtc.Ticks,
                TargetHash = targetHash
            };
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, ManifestJsonOptions));
        }

        private static string BuildKey(string kind, string relativePath, string targetPath)
        {
            return string.Join("|", kind.Trim().ToLowerInvariant(), NormalizePath(relativePath), NormalizePath(targetPath));
        }

        private static string NormalizePath(string path)
        {
            return path.Replace('\\', '/').Trim().ToLowerInvariant();
        }

        private static string ComputeHash(string path)
        {
            using var stream = File.OpenRead(path);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash);
        }
    }

    private sealed class InstallerDeploymentHashEntry
    {
        public long SourceLength { get; set; }
        public long SourceLastWriteTimeUtcTicks { get; set; }
        public string SourceHash { get; set; } = string.Empty;
        public long TargetLength { get; set; }
        public long TargetLastWriteTimeUtcTicks { get; set; }
        public string TargetHash { get; set; } = string.Empty;
    }

    private async Task WriteLogAsync(
        string logPath,
        InstallerDeploymentResult result,
        bool writeLog,
        CancellationToken cancellationToken)
    {
        if (!writeLog)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTimeOffset.Now,
                result.Action,
                result.DryRun,
                result.CheckedFiles,
                result.MissingFiles,
                result.ChangedFiles,
                result.CopiedFiles,
                result.Warnings
            }, LogJsonOptions);

            await File.AppendAllTextAsync(logPath, line + Environment.NewLine, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to write installer deployment log.");
        }
    }
}
