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
        var cachePath = ResolvePluginPath(deploymentOptions.CachePath);

        var result = new RetroArchWrapperDeploymentResult
        {
            Action = action,
            AutoDeploy = deploymentOptions.AutoDeploy,
            DryRun = dryRun,
            WrapperDllPath = wrapperPath,
            CoresPath = coresPath,
            RealCoresPath = realCoresPath,
            RetroArchRunning = IsRetroArchRunning()
        };

        result.WrapperExists = File.Exists(wrapperPath);
        result.WrapperHasSignature = result.WrapperExists && PorteLaSignature(wrapperPath);

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

        // L'empreinte du build de reference, UNE fois : l'ancienne version la recalculait
        // pour chacun des 157 cores.
        var wrapperReference = new WrapperReference(new FileInfo(wrapperPath));
        var cache = AuditCache.Charger(cachePath);
        var coreFiles = GetTargetCoreFiles(coresPath, deploymentOptions);
        foreach (var coreFile in coreFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = BuildCoreStatus(coreFile, realCoresPath, wrapperReference, cache);
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

        // Apres les copies : ce qu'on vient d'ecrire est relu au prochain audit (sa date a
        // change), et le cache ne garde que ce qui existe encore.
        cache.Enregistrer(cachePath, result.Cores.Select(c => c.CoreName), _logger);

        await WriteLogAsync(logPath, result, writeLog, cancellationToken);
        return result;
    }

    /// <summary>
    /// Ce que l'audit sait deja de chaque core : sa taille et sa date, et ce qu'on en a conclu
    /// (wrapper ou pas, empreinte). Un fichier qui n'a pas bouge n'est pas relu. Mesure sur une
    /// borne : l'audit sans rien a faire passait de 37 s (157 DLL lues deux fois, inspectees par
    /// l'antivirus a chaque ouverture) a une enumeration.
    /// </summary>
    private sealed class AuditCache
    {
        public sealed class Entree
        {
            public long Length { get; set; }
            public long LastWriteUtcTicks { get; set; }
            public bool IsWrapper { get; set; }
            public string? Md5 { get; set; }
        }

        private readonly Dictionary<string, Entree> _entrees;
        private bool _modifie;

        private AuditCache(Dictionary<string, Entree> entrees) => _entrees = entrees;

        public static AuditCache Charger(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var lu = JsonSerializer.Deserialize<Dictionary<string, Entree>>(File.ReadAllText(path));
                    if (lu is not null) return new AuditCache(new Dictionary<string, Entree>(lu, StringComparer.OrdinalIgnoreCase));
                }
            }
            catch
            {
                // Un cache illisible vaut un cache absent : on relit tout, une fois.
            }
            return new AuditCache(new Dictionary<string, Entree>(StringComparer.OrdinalIgnoreCase));
        }

        public Entree? Connue(FileInfo file)
        {
            return _entrees.TryGetValue(file.Name, out var e)
                && e.Length == file.Length
                && e.LastWriteUtcTicks == file.LastWriteTimeUtc.Ticks
                ? e
                : null;
        }

        public void Retenir(FileInfo file, bool isWrapper, byte[]? md5)
        {
            _entrees[file.Name] = new Entree
            {
                Length = file.Length,
                LastWriteUtcTicks = file.LastWriteTimeUtc.Ticks,
                IsWrapper = isWrapper,
                Md5 = md5 is null ? null : Convert.ToHexString(md5),
            };
            _modifie = true;
        }

        public void Enregistrer(string path, IEnumerable<string> presents, ILogger logger)
        {
            var garder = new HashSet<string>(presents, StringComparer.OrdinalIgnoreCase);
            var disparus = _entrees.Keys.Where(k => !garder.Contains(k)).ToList();
            foreach (var k in disparus) _entrees.Remove(k);
            if (!_modifie && disparus.Count == 0) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(_entrees));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "RetroArch wrapper audit cache not saved.");
            }
        }
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

    /// <summary>
    /// Le build de reference : son empreinte est calculee une fois, et sa taille borne ce
    /// qu'on accepte de lire. Un wrapper fait quelques centaines de kilo-octets ; un vrai
    /// core en fait des dizaines de mega-octets. Chercher la signature dans un fichier
    /// quatre fois plus gros que la reference, c'est lire un vrai core pour rien, et c'est
    /// ce qui coutait deux gigaoctets de lecture a chaque demarrage.
    /// </summary>
    private sealed class WrapperReference
    {
        public WrapperReference(FileInfo file)
        {
            File = file;
            MaxWrapperBytes = Math.Max(4 * file.Length, 4L * 1024 * 1024);
            using var md5 = System.Security.Cryptography.MD5.Create();
            using var stream = file.OpenRead();
            Md5 = md5.ComputeHash(stream);
        }

        public FileInfo File { get; }
        public long MaxWrapperBytes { get; }
        public byte[] Md5 { get; }
    }

    private static RetroArchWrapperCoreStatus BuildCoreStatus(
        FileInfo coreFile, string realCoresPath, WrapperReference wrapperReference, AuditCache cache)
    {
        bool isWrapper;
        byte[]? md5;
        var connue = cache.Connue(coreFile);
        if (connue is not null)
        {
            isWrapper = connue.IsWrapper;
            md5 = connue.Md5 is null ? null : Convert.FromHexString(connue.Md5);
        }
        else if (coreFile.Length > wrapperReference.MaxWrapperBytes)
        {
            // Trop gros pour etre un wrapper : un vrai core, qu'on ne lit pas.
            isWrapper = false;
            md5 = null;
            cache.Retenir(coreFile, false, null);
        }
        else
        {
            // UNE lecture : l'empreinte dit deja si c'est le build de reference ; sinon on
            // cherche la signature dans ce qu'on vient de lire.
            var contenu = File.ReadAllBytes(coreFile.FullName);
            md5 = System.Security.Cryptography.MD5.HashData(contenu);
            isWrapper = md5.AsSpan().SequenceEqual(wrapperReference.Md5)
                || contenu.AsSpan().IndexOf(WrapperSignature) >= 0;
            cache.Retenir(coreFile, isWrapper, md5);
        }

        var realCorePath = Path.Combine(realCoresPath, coreFile.Name);
        var realCore = new FileInfo(realCorePath);
        var hasRealCore = realCore.Exists;
        var estLaReference = md5 is not null
            && coreFile.Length == wrapperReference.File.Length
            && md5.AsSpan().SequenceEqual(wrapperReference.Md5);
        var needsRefresh = isWrapper && hasRealCore && !estLaReference;

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

    /// <summary>Le build de reference est-il bien un wrapper ? Lu une fois par audit.</summary>
    private static bool PorteLaSignature(string path)
    {
        try
        {
            return File.ReadAllBytes(path).AsSpan().IndexOf(WrapperSignature) >= 0;
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
