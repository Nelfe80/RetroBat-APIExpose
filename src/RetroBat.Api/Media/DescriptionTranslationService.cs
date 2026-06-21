using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetroBat.Api.Infrastructure;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Media;

public sealed class DescriptionTranslationService : BackgroundService
{
    private const string SourceLanguageDefault = "en";
    private const string ProgressTaskId = "description-translation-model";
    private static readonly Regex CliModelIdRegex = new(@"do -[dm]\s+(?<id>[A-Za-z0-9_+-]+-[A-Za-z0-9_+-]+-[A-Za-z0-9_+-]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CliPercentRegex = new(@"(?<percent>\d{1,3})%", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly ILocalizedTextStore _localizedTextStore;
    private readonly GamelistUpdateService _gamelistUpdateService;
    private readonly MediaRuntimeState _runtimeState;
    private readonly IEmulationStationNotificationService _notifications;
    private readonly ITaskProgressService _taskProgress;
    private readonly ILogger<DescriptionTranslationService>? _logger;
    private readonly Channel<bool> _wakeups = Channel.CreateUnbounded<bool>();
    private readonly ConcurrentDictionary<string, byte> _notifiedModelPreparations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelLocks = new(StringComparer.OrdinalIgnoreCase);
    private int _isProcessing;

    public DescriptionTranslationService(
        IOptionsMonitor<ApiExposeOptions> options,
        ILocalizedTextStore localizedTextStore,
        GamelistUpdateService gamelistUpdateService,
        MediaRuntimeState runtimeState,
        IEmulationStationNotificationService notifications,
        ITaskProgressService taskProgress,
        ILogger<DescriptionTranslationService>? logger = null)
    {
        _options = options;
        _localizedTextStore = localizedTextStore;
        _gamelistUpdateService = gamelistUpdateService;
        _runtimeState = runtimeState;
        _notifications = notifications;
        _taskProgress = taskProgress;
        _logger = logger;
    }

    public async Task<DescriptionTranslationScheduleResult> ScheduleFromScrapeAsync(
        MediaProjectionPlan plan,
        string textSlug,
        string requestedLanguage,
        string targetDescription,
        string englishDescription,
        bool hasScrapedEvidence,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue.Scraping;
        if (!options.DescriptionTranslationEnabled || !hasScrapedEvidence)
        {
            return DescriptionTranslationScheduleResult.NoAction;
        }

        var sourceLanguage = NormalizeLanguage(options.DescriptionTranslationSourceLanguage, SourceLanguageDefault);
        var targetLanguage = NormalizeLanguage(requestedLanguage, string.Empty);
        if (string.IsNullOrWhiteSpace(targetLanguage) ||
            string.Equals(targetLanguage, sourceLanguage, StringComparison.OrdinalIgnoreCase) ||
            !IsUnsatisfiedTargetDescription(targetDescription, englishDescription))
        {
            return DescriptionTranslationScheduleResult.NoAction;
        }

        var sourceDescription = LocalizedMetadataSanitizer.SanitizeText(englishDescription);
        if (string.IsNullOrWhiteSpace(sourceDescription))
        {
            return DescriptionTranslationScheduleResult.NoAction;
        }

        var pending = PendingDescriptionTranslation.FromPlan(
            plan,
            string.IsNullOrWhiteSpace(textSlug) ? plan.GameSlug : textSlug,
            sourceLanguage,
            targetLanguage,
            sourceDescription,
            "missing-target-desc");

        var pendingPath = GetPendingPath(pending);
        Directory.CreateDirectory(Path.GetDirectoryName(pendingPath)!);
        await WritePendingAsync(pendingPath, pending, cancellationToken);

        var modelStatus = await RunTranslateLocallyAsync(
            sourceLanguage,
            targetLanguage,
            text: string.Empty,
            installMissing: false,
            checkOnly: true,
            cancellationToken);

        if (string.Equals(modelStatus.Status, "installed", StringComparison.OrdinalIgnoreCase))
        {
            var applied = await ProcessPendingFileAsync(pendingPath, installMissing: false, cancellationToken);
            return applied
                ? new DescriptionTranslationScheduleResult(true, true)
                : new DescriptionTranslationScheduleResult(true, false);
        }

        if (options.DescriptionTranslationInstallMissingModels)
        {
            await NotifyModelPreparationAsync(sourceLanguage, targetLanguage, cancellationToken);
            WakePendingProcessor();
        }

        return new DescriptionTranslationScheduleResult(true, false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WakePendingProcessor();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wakeups.Reader.WaitToReadAsync(stoppingToken);
                while (_wakeups.Reader.TryRead(out _))
                {
                }

                await ProcessPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Pending description translation processor failed.");
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
        }
    }

    private void WakePendingProcessor()
    {
        _wakeups.Writer.TryWrite(true);
    }

    private async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _isProcessing, 1) == 1)
        {
            return;
        }

        try
        {
            foreach (var root in GetPendingRoots())
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).OrderBy(File.GetLastWriteTimeUtc))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ProcessPendingFileAsync(
                        file,
                        installMissing: _options.CurrentValue.Scraping.DescriptionTranslationInstallMissingModels,
                        cancellationToken);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isProcessing, 0);
        }
    }

    private async Task<bool> ProcessPendingFileAsync(string pendingPath, bool installMissing, CancellationToken cancellationToken)
    {
        var pending = await ReadPendingAsync(pendingPath, cancellationToken);
        if (pending == null)
        {
            TryDelete(pendingPath);
            return false;
        }

        if (!IsPendingStillCurrent(pending))
        {
            TryDelete(pendingPath);
            return false;
        }

        var result = await RunTranslateLocallyAsync(
            pending.SourceLanguage,
            pending.TargetLanguage,
            pending.SourceDescription,
            installMissing,
            checkOnly: false,
            cancellationToken);

        if (!string.Equals(result.Status, "translated", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(result.Translation))
        {
            pending.Attempts++;
            pending.LastStatus = result.Status;
            pending.LastMessage = result.Message;
            pending.UpdatedAtUtc = DateTime.UtcNow;
            await WritePendingAsync(pendingPath, pending, CancellationToken.None);
            return false;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["desc"] = result.Translation,
            ["lang"] = pending.TargetLanguage,
            ["source"] = $"screenscraper;translateLocally:desc:{pending.SourceLanguage}->{pending.TargetLanguage}"
        };

        var updated = await _localizedTextStore.PersistFieldsAsync(
            pending.SystemId,
            pending.TextSlug,
            pending.TargetLanguage,
            fields,
            cancellationToken);

        TryDelete(pendingPath);
        if (!updated)
        {
            return false;
        }

        var plan = pending.ToPlan();
        _gamelistUpdateService.MarkLiveGamelistDirty(plan);
        var staged = await _gamelistUpdateService.StageExtendedEntriesAsync(plan, cancellationToken);
        _runtimeState.RequestLocalizedGamelistCacheRefreshForGame(plan);
        _runtimeState.MarkReloadGamesPending(requestedByScrape: true);

        await MediaUpdateAuditLog.AppendAsync(
            plan,
            "description-translation",
            "metadata",
            "translated-desc",
            new
            {
                pending.SourceLanguage,
                pending.TargetLanguage,
                staged.Changed,
                staged.MetadataChanged,
                result.ModelDownloaded
            },
            cancellationToken);

        if (_options.CurrentValue.Scraping.DescriptionTranslationNotifyTranslatedCurrentGame &&
            _gamelistUpdateService.IsCurrentlySelectedGame(plan))
        {
            await _notifications.NotifyAsync(
                $"Description traduite en {pending.TargetLanguage} : {ResolveDisplayName(pending)}",
                cancellationToken);
        }

        return true;
    }

    private async Task NotifyModelPreparationAsync(string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        if (!_options.CurrentValue.Scraping.DescriptionTranslationNotifyModelInstall)
        {
            return;
        }

        var key = $"{sourceLanguage}->{targetLanguage}";
        if (!_notifiedModelPreparations.TryAdd(key, 0))
        {
            return;
        }

        await _notifications.NotifyAsync(
            $"Preparation du modele de traduction {sourceLanguage.ToUpperInvariant()} -> {targetLanguage.ToUpperInvariant()}",
            cancellationToken);
    }

    private async Task<TranslationToolResult> RunTranslateLocallyAsync(
        string sourceLanguage,
        string targetLanguage,
        string text,
        bool installMissing,
        bool checkOnly,
        CancellationToken cancellationToken)
    {
        var source = NormalizeLanguage(sourceLanguage, SourceLanguageDefault);
        var target = NormalizeLanguage(targetLanguage, string.Empty);
        var toolsRoot = ResolvePluginPath(_options.CurrentValue.Scraping.TranslateLocallyToolsPath);
        var executablePath = Path.Combine(toolsRoot, _options.CurrentValue.Scraping.TranslateLocallyExecutableName);
        var profileRoot = ResolvePluginPath(_options.CurrentValue.Scraping.TranslateLocallyProfilePath);
        var modelStoreRoot = ResolvePluginPath(_options.CurrentValue.Scraping.TranslateLocallyModelStorePath);
        if (!File.Exists(executablePath))
        {
            return new TranslationToolResult { Status = "missing-tool", Message = executablePath };
        }

        var key = $"{source}->{target}";
        var gate = _modelLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, _options.CurrentValue.Scraping.DescriptionTranslationTimeoutSeconds)));

            if (_options.CurrentValue.Scraping.TranslateLocallyPortableModelStoreEnabled)
            {
                var modelStore = await EnsurePortableModelStoreAsync(modelStoreRoot, timeoutCts.Token);
                if (!modelStore.Ready)
                {
                    return new TranslationToolResult { Status = modelStore.Status, Message = modelStore.Message };
                }
            }

            var localModel = await ResolveLocalModelAsync(executablePath, toolsRoot, profileRoot, source, target, timeoutCts.Token);
            var modelDownloaded = false;
            if (string.IsNullOrWhiteSpace(localModel))
            {
                if (checkOnly || !installMissing)
                {
                    return new TranslationToolResult { Status = "model-missing", Message = key };
                }

                var remoteModel = await ResolveRemoteModelAsync(executablePath, toolsRoot, profileRoot, source, target, timeoutCts.Token);
                if (string.IsNullOrWhiteSpace(remoteModel))
                {
                    return new TranslationToolResult { Status = "model-unavailable", Message = key };
                }

                await DownloadModelAsync(executablePath, toolsRoot, profileRoot, remoteModel, source, target, timeoutCts.Token);
                modelDownloaded = true;
                localModel = await ResolveLocalModelAsync(executablePath, toolsRoot, profileRoot, source, target, timeoutCts.Token)
                    ?? remoteModel;
            }

            if (checkOnly)
            {
                return new TranslationToolResult { Status = "installed" };
            }

            var translation = await TranslateAsync(executablePath, toolsRoot, profileRoot, localModel, text, timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(translation))
            {
                return new TranslationToolResult { Status = "empty-translation" };
            }

            return new TranslationToolResult
            {
                Status = "translated",
                Translation = translation,
                ModelDownloaded = modelDownloaded
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new TranslationToolResult { Status = "timeout" };
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "TranslateLocally CLI invocation failed.");
            return new TranslationToolResult { Status = "failed", Message = ex.Message };
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<ModelStorePreparationResult> EnsurePortableModelStoreAsync(
        string modelStoreRoot,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(modelStoreRoot);
            return ModelStorePreparationResult.Success;
        }

        var roamingRoot = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(roamingRoot))
        {
            return ModelStorePreparationResult.Failed("model-store-error", "APPDATA roaming path is unavailable.");
        }

        var globalRoot = Path.Combine(roamingRoot, "translateLocally");
        Directory.CreateDirectory(modelStoreRoot);

        if (IsDirectoryLinkedTo(globalRoot, modelStoreRoot))
        {
            return ModelStorePreparationResult.Success;
        }

        try
        {
            if (Directory.Exists(globalRoot))
            {
                CopyDirectory(globalRoot, modelStoreRoot);
                var backupPath = BuildBackupPath(globalRoot);
                Directory.Move(globalRoot, backupPath);
                _logger?.LogInformation(
                    "translateLocally global model directory migrated to {ModelStoreRoot}; backup kept at {BackupPath}.",
                    modelStoreRoot,
                    backupPath);
            }

            var result = await RunProcessAsync(
                "cmd.exe",
                Directory.GetCurrentDirectory(),
                ["/c", "mklink", "/J", globalRoot, modelStoreRoot],
                cancellationToken);

            if (result.ExitCode != 0 || !Directory.Exists(globalRoot))
            {
                return ModelStorePreparationResult.Failed(
                    "model-store-error",
                    result.StandardError.TrimOrFallback(result.StandardOutput));
            }

            return ModelStorePreparationResult.Success;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not prepare translateLocally portable model store.");
            return ModelStorePreparationResult.Failed("model-store-error", ex.Message);
        }
    }

    private static bool IsDirectoryLinkedTo(string linkPath, string targetPath)
    {
        if (!Directory.Exists(linkPath))
        {
            return false;
        }

        try
        {
            var info = new DirectoryInfo(linkPath);
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
            return resolved != null &&
                string.Equals(
                    Path.GetFullPath(resolved.FullName).TrimEnd('\\'),
                    Path.GetFullPath(targetPath).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var targetFile = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            if (!File.Exists(targetFile))
            {
                File.Copy(sourceFile, targetFile);
            }
        }
    }

    private static string BuildBackupPath(string path)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupPath = $"{path}.apiexpose-backup-{stamp}";
        var index = 1;
        while (Directory.Exists(backupPath))
        {
            backupPath = $"{path}.apiexpose-backup-{stamp}-{index++}";
        }

        return backupPath;
    }

    private async Task<string?> ResolveLocalModelAsync(
        string executablePath,
        string workingDirectory,
        string profileRoot,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var result = await RunCliAsync(
            executablePath,
            workingDirectory,
            profileRoot,
            ["-l"],
            inputText: null,
            stdoutLine: null,
            cancellationToken);

        return result.ExitCode == 0
            ? SelectModelId(result.StandardOutput, sourceLanguage, targetLanguage)
            : null;
    }

    private async Task<string?> ResolveRemoteModelAsync(
        string executablePath,
        string workingDirectory,
        string profileRoot,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        _taskProgress.Report(
            ProgressTaskId,
            "Modele de traduction",
            0,
            100,
            $"{sourceLanguage.ToUpperInvariant()} -> {targetLanguage.ToUpperInvariant()}");

        var result = await RunCliAsync(
            executablePath,
            workingDirectory,
            profileRoot,
            ["-a"],
            inputText: null,
            stdoutLine: null,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            _taskProgress.Complete(ProgressTaskId);
            return null;
        }

        return SelectModelId(result.StandardOutput, sourceLanguage, targetLanguage);
    }

    private async Task DownloadModelAsync(
        string executablePath,
        string workingDirectory,
        string profileRoot,
        string modelId,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        var lastProgress = 0;
        _taskProgress.Report(
            ProgressTaskId,
            "Modele de traduction",
            1,
            100,
            $"{sourceLanguage.ToUpperInvariant()} -> {targetLanguage.ToUpperInvariant()}  {modelId}");

        try
        {
            var result = await RunCliAsync(
                executablePath,
                workingDirectory,
                profileRoot,
                ["-d", modelId],
                inputText: null,
                stdoutLine: line =>
                {
                    var match = CliPercentRegex.Match(line);
                    if (match.Success && int.TryParse(match.Groups["percent"].Value, out var percent))
                    {
                        lastProgress = Math.Max(lastProgress, Math.Min(100, percent));
                    }
                    else if (lastProgress < 95)
                    {
                        lastProgress = Math.Max(5, lastProgress + 1);
                    }

                    _taskProgress.Report(
                        ProgressTaskId,
                        "Modele de traduction",
                        Math.Max(1, lastProgress),
                        100,
                        $"{sourceLanguage.ToUpperInvariant()} -> {targetLanguage.ToUpperInvariant()}  {modelId}");
                },
                cancellationToken);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(result.StandardError.TrimOrFallback(result.StandardOutput));
            }

            _taskProgress.Report(ProgressTaskId, "Modele de traduction", 100, 100, "Telechargement termine");
        }
        finally
        {
            _taskProgress.Complete(ProgressTaskId);
        }
    }

    private static async Task<string> TranslateAsync(
        string executablePath,
        string workingDirectory,
        string profileRoot,
        string modelId,
        string text,
        CancellationToken cancellationToken)
    {
        var result = await RunCliAsync(
            executablePath,
            workingDirectory,
            profileRoot,
            ["-m", modelId],
            inputText: text,
            stdoutLine: null,
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.StandardError.TrimOrFallback(result.StandardOutput));
        }

        return result.StandardOutput.Trim();
    }

    private static async Task<CliResult> RunCliAsync(
        string executablePath,
        string workingDirectory,
        string profileRoot,
        IReadOnlyList<string> arguments,
        string? inputText,
        Action<string>? stdoutLine,
        CancellationToken cancellationToken)
    {
        var environment = PrepareProfileEnvironment(profileRoot);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = inputText != null,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (inputText != null)
        {
            startInfo.StandardInputEncoding = Encoding.UTF8;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var item in environment)
        {
            startInfo.Environment[item.Key] = item.Value;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start translateLocally.");

        var stdoutBuilder = new StringBuilder();
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = Task.Run(async () =>
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break;
                }

                stdoutBuilder.AppendLine(line);
                stdoutLine?.Invoke(line);
            }
        }, cancellationToken);

        if (inputText != null)
        {
            await process.StandardInput.WriteAsync(inputText.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(cancellationToken);
        await stdoutTask;
        var stderr = await stderrTask;

        return new CliResult(process.ExitCode, stdoutBuilder.ToString(), stderr);
    }

    private static async Task<CliResult> RunProcessAsync(
        string executablePath,
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executablePath}.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new CliResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static Dictionary<string, string> PrepareProfileEnvironment(string profileRoot)
    {
        Directory.CreateDirectory(profileRoot);
        var roamingRoot = Path.Combine(profileRoot, "AppData", "Roaming");
        var localRoot = Path.Combine(profileRoot, "AppData", "Local");
        var cacheRoot = Path.Combine(profileRoot, "cache");
        var dataRoot = Path.Combine(profileRoot, "data");
        var configRoot = Path.Combine(profileRoot, "config");
        Directory.CreateDirectory(roamingRoot);
        Directory.CreateDirectory(localRoot);
        Directory.CreateDirectory(cacheRoot);
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(configRoot);

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["APPDATA"] = roamingRoot,
            ["LOCALAPPDATA"] = localRoot,
            ["XDG_CACHE_HOME"] = cacheRoot,
            ["XDG_DATA_HOME"] = dataRoot,
            ["XDG_CONFIG_HOME"] = configRoot
        };
    }

    private static string? SelectModelId(string output, string sourceLanguage, string targetLanguage)
    {
        var candidates = CliModelIdRegex.Matches(output ?? string.Empty)
            .Select(match => match.Groups["id"].Value.Trim())
            .Where(id => ModelMatches(id, sourceLanguage, targetLanguage))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id.Contains("-tiny", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates.FirstOrDefault();
    }

    private static bool ModelMatches(string modelId, string sourceLanguage, string targetLanguage)
    {
        var parts = modelId.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 3 &&
            string.Equals(NormalizeLanguage(parts[0], string.Empty), sourceLanguage, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeLanguage(parts[1], string.Empty), targetLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<string> GetPendingRoots()
    {
        yield return ResolvePluginPath(_options.CurrentValue.Scraping.DescriptionTranslationPendingPath);

        var legacyArgosPath = ResolvePluginPath("media/aliases/shared/argos-translation-pending");
        if (!string.Equals(
            legacyArgosPath,
            ResolvePluginPath(_options.CurrentValue.Scraping.DescriptionTranslationPendingPath),
            StringComparison.OrdinalIgnoreCase))
        {
            yield return legacyArgosPath;
        }
    }

    private static async Task<PendingDescriptionTranslation?> ReadPendingAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return await JsonSerializer.DeserializeAsync<PendingDescriptionTranslation>(stream, JsonOptions, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WritePendingAsync(string path, PendingDescriptionTranslation pending, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, pending, JsonOptions, cancellationToken);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    private string GetPendingPath(PendingDescriptionTranslation pending)
    {
        var root = ResolvePluginPath(_options.CurrentValue.Scraping.DescriptionTranslationPendingPath);
        var fileName = string.Join(
            "-",
            SafeFilePart(pending.FrontendSystemId),
            SafeFilePart(pending.TextSlug),
            SafeFilePart(pending.TargetLanguage),
            pending.SourceDescriptionSha256[..Math.Min(12, pending.SourceDescriptionSha256.Length)]) + ".json";
        return Path.Combine(root, fileName);
    }

    private static bool IsPendingStillCurrent(PendingDescriptionTranslation pending)
    {
        return string.Equals(
            pending.SourceDescriptionSha256,
            ComputeSha256(pending.SourceDescription),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnsatisfiedTargetDescription(string targetDescription, string englishDescription)
    {
        var target = LocalizedMetadataSanitizer.SanitizeText(targetDescription);
        if (string.IsNullOrWhiteSpace(target))
        {
            return true;
        }

        var english = LocalizedMetadataSanitizer.SanitizeText(englishDescription);
        return !string.IsNullOrWhiteSpace(english) &&
            string.Equals(target, english, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolvePluginPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RetroBatPaths.PluginRoot;
        }

        return Path.IsPathRooted(value)
            ? value
            : Path.GetFullPath(Path.Combine(RetroBatPaths.PluginRoot, value));
    }

    private static string NormalizeLanguage(string value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        return normalized.Length >= 2 ? normalized[..2] : fallback;
    }

    private static string SafeFilePart(string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value)
        {
            builder.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');
        }

        var safe = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }

    private static string ComputeSha256(string value)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
    }

    private static string ResolveDisplayName(PendingDescriptionTranslation pending)
    {
        return string.IsNullOrWhiteSpace(pending.DisplayName)
            ? pending.GameSlug
            : pending.DisplayName;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record ModelStorePreparationResult(bool Ready, string Status, string Message)
    {
        public static ModelStorePreparationResult Success { get; } = new(true, "ready", string.Empty);
        public static ModelStorePreparationResult Failed(string status, string message) => new(false, status, message);
    }

    private sealed class PendingDescriptionTranslation
    {
        public string FrontendSystemId { get; set; } = string.Empty;
        public string SystemId { get; set; } = string.Empty;
        public string GameSlug { get; set; } = string.Empty;
        public string TextSlug { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string GamePath { get; set; } = string.Empty;
        public string ProjectionBaseName { get; set; } = string.Empty;
        public string GamelistPath { get; set; } = string.Empty;
        public string SourceLanguage { get; set; } = SourceLanguageDefault;
        public string TargetLanguage { get; set; } = string.Empty;
        public string SourceDescription { get; set; } = string.Empty;
        public string SourceDescriptionSha256 { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public int Attempts { get; set; }
        public string LastStatus { get; set; } = string.Empty;
        public string LastMessage { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public static PendingDescriptionTranslation FromPlan(
            MediaProjectionPlan plan,
            string textSlug,
            string sourceLanguage,
            string targetLanguage,
            string sourceDescription,
            string reason)
        {
            return new PendingDescriptionTranslation
            {
                FrontendSystemId = plan.FrontendSystemId,
                SystemId = plan.SystemId,
                GameSlug = plan.GameSlug,
                TextSlug = textSlug,
                DisplayName = plan.DisplayName,
                GamePath = plan.GamePath,
                ProjectionBaseName = plan.ProjectionBaseName,
                GamelistPath = plan.GamelistPath,
                SourceLanguage = sourceLanguage,
                TargetLanguage = targetLanguage,
                SourceDescription = sourceDescription,
                SourceDescriptionSha256 = ComputeSha256(sourceDescription),
                Reason = reason
            };
        }

        public MediaProjectionPlan ToPlan()
        {
            return new MediaProjectionPlan
            {
                FrontendSystemId = FrontendSystemId,
                SystemId = string.IsNullOrWhiteSpace(SystemId) ? FrontendSystemId : SystemId,
                GameSlug = GameSlug,
                TextSourceGameSlug = TextSlug,
                DisplayName = DisplayName,
                GamePath = GamePath,
                ProjectionBaseName = ProjectionBaseName,
                GamelistPath = string.IsNullOrWhiteSpace(GamelistPath)
                    ? Path.Combine(RetroBatPaths.RomsRoot, FrontendSystemId, "gamelist.xml")
                    : GamelistPath
            };
        }
    }

    private sealed class TranslationToolResult
    {
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
        public bool ModelDownloaded { get; set; }
    }
}

public sealed record DescriptionTranslationScheduleResult(bool PendingQueued, bool TranslationApplied)
{
    public static DescriptionTranslationScheduleResult NoAction { get; } = new(false, false);
}

file static class DescriptionTranslationStringExtensions
{
    public static string TrimOrFallback(this string value, string fallback)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? (fallback ?? string.Empty).Trim()
            : trimmed;
    }
}
