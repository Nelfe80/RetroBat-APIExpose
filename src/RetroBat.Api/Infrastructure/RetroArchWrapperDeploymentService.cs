using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public class RetroArchWrapperDeploymentService
{
    private static readonly byte[] WrapperSignature = Encoding.ASCII.GetBytes("RETROBAT_ARCADE_WRAPPER_V1_DO_NOT_DELETE");
    private static readonly JsonSerializerOptions LogJsonOptions = new() { WriteIndented = false };

    private readonly IOptions<ApiExposeOptions> _options;
    private readonly ILogger<RetroArchWrapperDeploymentService> _logger;

    public RetroArchWrapperDeploymentService(
        IOptions<ApiExposeOptions> options,
        ILogger<RetroArchWrapperDeploymentService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task<RetroArchWrapperDeploymentResult> AuditAsync(CancellationToken cancellationToken = default)
    {
        return ExecuteAsync("audit", dryRun: true, writeLog: false, cancellationToken);
    }

    public Task<RetroArchWrapperDeploymentResult> DeployAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync("deploy", dryRun, writeLog: true, cancellationToken);
    }

    private async Task<RetroArchWrapperDeploymentResult> ExecuteAsync(
        string action,
        bool dryRun,
        bool writeLog,
        CancellationToken cancellationToken)
    {
        var deploymentOptions = _options.Value.RetroArchWrapperDeployment;
        var wrapperPath = ResolvePluginPath(deploymentOptions.WrapperDllPath);
        var coresPath = ResolvePluginPath(deploymentOptions.CoresPath);
        var realCoresPath = ResolvePluginPath(deploymentOptions.RealCoresPath);
        var backupRoot = ResolvePluginPath(deploymentOptions.BackupPath);
        var logPath = ResolvePluginPath(deploymentOptions.LogFilePath);

        var result = new RetroArchWrapperDeploymentResult
        {
            Action = action,
            Enabled = deploymentOptions.Enabled,
            DryRun = dryRun,
            WrapperDllPath = wrapperPath,
            CoresPath = coresPath,
            RealCoresPath = realCoresPath,
            RetroArchRunning = IsRetroArchRunning()
        };

        if (!deploymentOptions.Enabled && action.Equals("deploy", StringComparison.OrdinalIgnoreCase))
        {
            result.Warnings.Add("RetroArch wrapper deployment is disabled in appsettings.");
            await WriteLogAsync(logPath, result, writeLog, cancellationToken);
            return result;
        }

        result.WrapperExists = File.Exists(wrapperPath);
        result.WrapperHasSignature = result.WrapperExists && IsWrapperFile(wrapperPath);

        if (!result.WrapperExists)
        {
            result.Warnings.Add($"Wrapper DLL not found: {wrapperPath}");
            await WriteLogAsync(logPath, result, writeLog, cancellationToken);
            return result;
        }

        if (!result.WrapperHasSignature)
        {
            result.Warnings.Add($"Wrapper DLL does not contain the expected signature: {wrapperPath}");
            await WriteLogAsync(logPath, result, writeLog, cancellationToken);
            return result;
        }

        if (!Directory.Exists(coresPath))
        {
            result.Warnings.Add($"RetroArch cores directory not found: {coresPath}");
            await WriteLogAsync(logPath, result, writeLog, cancellationToken);
            return result;
        }

        if (deploymentOptions.SkipIfRetroArchRunning && result.RetroArchRunning && action.Equals("deploy", StringComparison.OrdinalIgnoreCase))
        {
            result.SkippedBecauseRetroArchRunning = true;
            result.Warnings.Add("RetroArch is running; deployment skipped to avoid touching loaded core DLLs.");
            await WriteLogAsync(logPath, result, writeLog, cancellationToken);
            return result;
        }

        var wrapperReference = new FileInfo(wrapperPath);
        var coreFiles = GetTargetCoreFiles(coresPath, deploymentOptions);
        foreach (var coreFile in coreFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = BuildCoreStatus(coreFile, realCoresPath, wrapperReference);
            result.Cores.Add(status);
        }

        result.CheckedCores = result.Cores.Count;
        result.WrappedCores = result.Cores.Count(core => core.IsWrapper);
        result.RealCores = result.Cores.Count(core => !core.IsWrapper);
        result.MissingRealCores = result.Cores.Count(core => core.IsWrapper && !core.HasRealCore);
        result.PendingDeployments = result.Cores.Count(core => core.NeedsDeployment);
        result.StaleWrappers = result.Cores.Count(core => core.NeedsRefresh);

        if (action.Equals("deploy", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var core in result.Cores.Where(core => core.NeedsDeployment))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeployCore(wrapperPath, realCoresPath, backupRoot, dryRun, core, result);
            }

            // Un wrapper deja en place mais different du build de reference est
            // simplement recopie (jamais deplace vers cores_real : c'est le vrai
            // core qui s'y trouve). Corrige la derive de versions de la flotte.
            foreach (var core in result.Cores.Where(core => core.NeedsRefresh))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RefreshCore(wrapperPath, backupRoot, dryRun, core, result);
            }
        }

        await WriteLogAsync(logPath, result, writeLog, cancellationToken);
        return result;
    }

    private static IEnumerable<FileInfo> GetTargetCoreFiles(
        string coresPath,
        ApiExposeOptions.RetroArchWrapperDeploymentOptions options)
    {
        if (options.WrapAllCores)
        {
            return new DirectoryInfo(coresPath)
                .EnumerateFiles("*.dll", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var targetNames = options.TargetCores
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return targetNames
            .Select(name => new FileInfo(Path.Combine(coresPath, name)))
            .Where(file => file.Exists)
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static RetroArchWrapperCoreStatus BuildCoreStatus(FileInfo coreFile, string realCoresPath, FileInfo wrapperReference)
    {
        var isWrapper = IsWrapperFile(coreFile.FullName);
        var realCorePath = Path.Combine(realCoresPath, coreFile.Name);
        var realCore = new FileInfo(realCorePath);
        var hasRealCore = realCore.Exists;
        var needsRefresh = isWrapper && hasRealCore && !FilesHaveSameContent(coreFile, wrapperReference);

        var reason = isWrapper
            ? needsRefresh
                ? "Wrapper deployed but outdated; it will be refreshed with the reference build."
                : hasRealCore ? "Wrapper deployed and real core available." : "Wrapper deployed but real core is missing."
            : hasRealCore ? "Real core detected in cores; cores_real will be backed up and refreshed." : "Real core detected in cores; it will be moved to cores_real.";

        return new RetroArchWrapperCoreStatus
        {
            CoreName = coreFile.Name,
            CorePath = coreFile.FullName,
            RealCorePath = realCorePath,
            IsWrapper = isWrapper,
            HasRealCore = hasRealCore,
            NeedsDeployment = !isWrapper,
            NeedsRefresh = needsRefresh,
            CoreBytes = coreFile.Length,
            RealCoreBytes = hasRealCore ? realCore.Length : null,
            LastWriteTime = coreFile.LastWriteTime,
            RealLastWriteTime = hasRealCore ? realCore.LastWriteTime : null,
            Reason = reason
        };
    }

    private static bool FilesHaveSameContent(FileInfo left, FileInfo right)
    {
        if (!left.Exists || !right.Exists)
        {
            return false;
        }

        if (left.Length != right.Length)
        {
            return false;
        }

        using var md5 = System.Security.Cryptography.MD5.Create();
        using var leftStream = left.OpenRead();
        var leftHash = md5.ComputeHash(leftStream);
        md5.Initialize();
        using var rightStream = right.OpenRead();
        var rightHash = md5.ComputeHash(rightStream);
        return leftHash.AsSpan().SequenceEqual(rightHash);
    }

    private static void RefreshCore(
        string wrapperPath,
        string backupRoot,
        bool dryRun,
        RetroArchWrapperCoreStatus core,
        RetroArchWrapperDeploymentResult result)
    {
        var backupPath = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"), core.CoreName);
        result.Actions.Add(new RetroArchWrapperDeploymentAction
        {
            CoreName = core.CoreName,
            Operation = "backup-stale-wrapper",
            SourcePath = core.CorePath,
            DestinationPath = backupPath,
            Applied = !dryRun
        });

        result.Actions.Add(new RetroArchWrapperDeploymentAction
        {
            CoreName = core.CoreName,
            Operation = "refresh-wrapper-in-cores",
            SourcePath = wrapperPath,
            DestinationPath = core.CorePath,
            Applied = !dryRun
        });

        if (dryRun)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Copy(core.CorePath, backupPath, overwrite: true);
        File.Copy(wrapperPath, core.CorePath, overwrite: true);
        result.RefreshedCores++;
    }

    private static void DeployCore(
        string wrapperPath,
        string realCoresPath,
        string backupRoot,
        bool dryRun,
        RetroArchWrapperCoreStatus core,
        RetroArchWrapperDeploymentResult result)
    {
        var backupPath = default(string);
        if (File.Exists(core.RealCorePath))
        {
            backupPath = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"), core.CoreName);
            result.Actions.Add(new RetroArchWrapperDeploymentAction
            {
                CoreName = core.CoreName,
                Operation = "backup-real-core",
                SourcePath = core.RealCorePath,
                DestinationPath = backupPath,
                Applied = !dryRun
            });

            if (!dryRun)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(core.RealCorePath, backupPath, overwrite: true);
            }
        }

        result.Actions.Add(new RetroArchWrapperDeploymentAction
        {
            CoreName = core.CoreName,
            Operation = "move-updated-core-to-cores-real",
            SourcePath = core.CorePath,
            DestinationPath = core.RealCorePath,
            BackupPath = backupPath,
            Applied = !dryRun
        });

        result.Actions.Add(new RetroArchWrapperDeploymentAction
        {
            CoreName = core.CoreName,
            Operation = "copy-wrapper-to-cores",
            SourcePath = wrapperPath,
            DestinationPath = core.CorePath,
            Applied = !dryRun
        });

        if (dryRun)
        {
            return;
        }

        Directory.CreateDirectory(realCoresPath);
        File.Move(core.CorePath, core.RealCorePath, overwrite: true);
        File.Copy(wrapperPath, core.CorePath, overwrite: true);
        result.DeployedCores++;
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

    private static bool IsRetroArchRunning()
    {
        try
        {
            return Process.GetProcessesByName("retroarch").Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWrapperFile(string path)
    {
        try
        {
            var content = File.ReadAllBytes(path);
            return content.AsSpan().IndexOf(WrapperSignature) >= 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task WriteLogAsync(
        string logPath,
        RetroArchWrapperDeploymentResult result,
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
                result.CheckedCores,
                result.PendingDeployments,
                result.DeployedCores,
                result.Warnings,
                result.Actions
            }, LogJsonOptions);

            await File.AppendAllTextAsync(logPath, line + Environment.NewLine, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to write RetroArch wrapper deployment log.");
        }
    }
}
