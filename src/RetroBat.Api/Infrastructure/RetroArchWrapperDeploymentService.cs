using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public class RetroArchWrapperDeploymentService
{
    private static readonly byte[] WrapperSignature = Encoding.ASCII.GetBytes("RETROBAT_ARCADE_WRAPPER_V1_DO_NOT_DELETE");
    private static readonly JsonSerializerOptions LogJsonOptions = new() { WriteIndented = false };

    /// <summary>L'ancien montage : le wrapper dans cores/, le vrai coeur dans cores_real/.</summary>
    public const string MontageCoresReal = "cores_real";
    /// <summary>Le montage propose par RetroBat : le vrai coeur reste dans cores/, le wrapper vit dans core_proxy/.</summary>
    public const string MontageCoreProxy = "core_proxy";
    // Ce que porte un lanceur qui sait passer core_proxy a RetroArch : une chaine .NET, donc en UTF-16.
    private static readonly byte[] MarqueCoreProxy = Encoding.Unicode.GetBytes("core_proxy");

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
        var coreProxyPath = ResolvePluginPath(deploymentOptions.CoreProxyPath);
        var lanceurGere = LanceurGereCoreProxy(ResolvePluginPath(deploymentOptions.EmulatorLauncherPath));

        var result = new RetroArchWrapperDeploymentResult
        {
            Layout = ChoisirMontage(deploymentOptions.Layout, lanceurGere),
            LauncherSupportsCoreProxy = lanceurGere,
            CoreProxyPath = coreProxyPath,
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

        if (result.Layout == MontageCoreProxy)
        {
            ExecuterEnCoreProxy(action, dryRun, wrapperPath, coresPath, realCoresPath, coreProxyPath, backupRoot,
                wrapperReference, cache, deploymentOptions, result, cancellationToken);
            cache.Enregistrer(cachePath, result.Cores.Select(c => c.CoreName), _logger);
            await WriteLogAsync(logPath, result, writeLog, cancellationToken);
            return result;
        }

        var coreFiles = GetTargetCoreFiles(coresPath, deploymentOptions);
        foreach (var coreFile in coreFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = BuildCoreStatus(
                coreFile, realCoresPath, wrapperReference, cache, deploymentOptions.ExcludedCores);
            result.Cores.Add(status);
        }

        result.CheckedCores = result.Cores.Count;
        result.ExcludedCores = result.Cores.Count(core => core.Excluded);
        result.WrappedCores = result.Cores.Count(core => core.IsWrapper);
        result.RealCores = result.Cores.Count(core => !core.IsWrapper);
        result.MissingRealCores = result.Cores.Count(core => core.IsWrapper && !core.HasRealCore);
        result.PendingDeployments = result.Cores.Count(core => core.NeedsDeployment);
        result.StaleWrappers = result.Cores.Count(core => core.NeedsRefresh);

        if (action.Equals("deploy", StringComparison.OrdinalIgnoreCase))
        {
            // Les exclusions d'abord : un coeur mis hors du wrapper doit retrouver le vrai
            // binaire avant qu'on ne touche aux autres, pour qu'un arret en cours de route ne
            // laisse pas en place un shim qu'on vient de decider de retirer.
            foreach (var core in result.Cores.Where(core => core.NeedsRestore))
            {
                cancellationToken.ThrowIfCancellationRequested();
                RestoreCore(backupRoot, dryRun, core, result);
            }

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

            // Un core_proxy/ laisse la (lanceur revenu a une version qui ne le lit pas, ou ancien
            // montage force) n'a plus de lecteur : on le retire, une fois les coeurs remis.
            RetirerCoreProxy(coreProxyPath, dryRun, result);
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

    /// <summary>
    /// Ce cœur est-il mis hors du wrapper par la configuration ? On compare sur le nom, avec ou
    /// sans le « .dll » : c'est « mame_libretro » qu'on écrit dans les appsettings, pas un chemin.
    /// </summary>
    internal static bool EstExclu(string coreFileName, IEnumerable<string> exclus) => EstNomme(coreFileName, exclus);

    /// <summary>Ce coeur figure-t-il dans cette liste de noms, avec ou sans le « .dll » ?</summary>
    internal static bool EstNomme(string coreFileName, IEnumerable<string> noms)
    {
        var nu = Path.GetFileNameWithoutExtension(coreFileName);
        foreach (var brut in noms)
        {
            if (string.IsNullOrWhiteSpace(brut))
            {
                continue;
            }

            var nom = Path.GetFileNameWithoutExtension(brut.Trim());
            if (string.Equals(nom, nu, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ce qu'il faut faire de ce cœur, en une règle lisible.
    ///
    /// Un cœur EXCLU ne reçoit rien et n'est jamais rafraîchi. S'il porte déjà le shim et que le
    /// vrai binaire est dans <c>cores_real</c>, il est REMIS EN PLACE : exclure sans défaire ne
    /// changerait rien sur une borne déjà déployée, ce qui est le cas de toutes.
    /// </summary>
    internal static (bool Restore, bool Deploy, bool Refresh) Arbitrer(
        bool exclu, bool isWrapper, bool hasRealCore, bool estLaReference)
    {
        if (exclu)
        {
            return (isWrapper && hasRealCore, false, false);
        }

        return (false, !isWrapper, isWrapper && hasRealCore && !estLaReference);
    }

    /// <summary>Ce fichier de cores/ est-il un wrapper ? Lu au plus une fois par version du fichier.</summary>
    private static (bool IsWrapper, byte[]? Md5) Inspecter(FileInfo coreFile, WrapperReference wrapperReference, AuditCache cache)
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

        return (isWrapper, md5);
    }

    private static RetroArchWrapperCoreStatus BuildCoreStatus(
        FileInfo coreFile, string realCoresPath, WrapperReference wrapperReference, AuditCache cache,
        IEnumerable<string>? exclus = null)
    {
        var (isWrapper, md5) = Inspecter(coreFile, wrapperReference, cache);
        var realCorePath = Path.Combine(realCoresPath, coreFile.Name);
        var realCore = new FileInfo(realCorePath);
        var hasRealCore = realCore.Exists;
        var estLaReference = md5 is not null
            && coreFile.Length == wrapperReference.File.Length
            && md5.AsSpan().SequenceEqual(wrapperReference.Md5);
        var exclu = exclus is not null && EstExclu(coreFile.Name, exclus);
        var (needsRestore, needsDeployment, needsRefresh) = Arbitrer(exclu, isWrapper, hasRealCore, estLaReference);

        var reason = exclu
            ? needsRestore
                ? "Core excluded from wrapping; the real core will be put back in cores."
                : isWrapper
                    ? "Core excluded from wrapping but the real core is missing from cores_real: nothing to put back."
                    : "Core excluded from wrapping; already the real core."
            : isWrapper
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
            Excluded = exclu,
            NeedsRestore = needsRestore,
            NeedsDeployment = needsDeployment,
            NeedsRefresh = needsRefresh,
            CoreBytes = coreFile.Length,
            RealCoreBytes = hasRealCore ? realCore.Length : null,
            LastWriteTime = coreFile.LastWriteTime,
            RealLastWriteTime = hasRealCore ? realCore.LastWriteTime : null,
            Reason = reason
        };
    }

    /// <summary>
    /// Remet le vrai cœur à sa place dans <c>cores/</c>, pour un cœur que la configuration met
    /// hors du wrapper. Le shim est sauvegardé d'abord, comme une dépose l'est : on ne détruit
    /// jamais ce qu'on remplace sans en garder une copie datée.
    ///
    /// La copie de <c>cores_real/</c> est LAISSÉE en place. Elle ne gêne rien, et si le cœur sort
    /// un jour de la liste d'exclusion, le déploiement la retrouve.
    /// </summary>
    private static void RestoreCore(
        string backupRoot,
        bool dryRun,
        RetroArchWrapperCoreStatus core,
        RetroArchWrapperDeploymentResult result)
    {
        var backupPath = Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"), core.CoreName);
        result.Actions.Add(new RetroArchWrapperDeploymentAction
        {
            CoreName = core.CoreName,
            Operation = "backup-wrapper-before-restore",
            SourcePath = core.CorePath,
            DestinationPath = backupPath,
            Applied = !dryRun
        });

        result.Actions.Add(new RetroArchWrapperDeploymentAction
        {
            CoreName = core.CoreName,
            Operation = "restore-real-core-to-cores",
            SourcePath = core.RealCorePath,
            DestinationPath = core.CorePath,
            BackupPath = backupPath,
            Applied = !dryRun
        });

        if (dryRun)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Copy(core.CorePath, backupPath, overwrite: true);
        File.Copy(core.RealCorePath, core.CorePath, overwrite: true);
        result.RestoredCores++;
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

    // ── Montage core_proxy (2026-10-04) ──────────────────────────────────────────────────────
    // Propose par l'equipe RetroBat : le vrai coeur reste dans cores/, ou RetroBat le verifie et le
    // met a jour, et un lanceur qui le sait passe a RetroArch core_proxy/<coeur>_libretro.dll quand
    // ce fichier existe. Le wrapper (0.341 et suivants) y trouve son vrai coeur dans cores/. Avec un
    // lanceur qui l'ignore, on garde l'ancien montage. Un seul wrapper : chaque entree de
    // core_proxy/ est un lien vers le meme fichier (une copie sur un disque sans liens, exFAT).

    private sealed record LanceurConnu(string Chemin, long Taille, DateTime Date, bool Gere);
    private static LanceurConnu? _lanceurConnu;

    /// <summary>Le montage a appliquer : celui que force la configuration, sinon d'apres le lanceur.</summary>
    internal static string ChoisirMontage(string? option, bool lanceurGere)
    {
        var choix = (option ?? string.Empty).Trim();
        if (string.Equals(choix, MontageCoreProxy, StringComparison.OrdinalIgnoreCase)) return MontageCoreProxy;
        if (string.Equals(choix, MontageCoresReal, StringComparison.OrdinalIgnoreCase)) return MontageCoresReal;
        return lanceurGere ? MontageCoreProxy : MontageCoresReal;
    }

    /// <summary>
    /// Le lanceur installe passe-t-il core_proxy a RetroArch ? Il porte alors la chaine
    /// « core_proxy » dans son exe. Lu une fois par version du fichier (quelques Mo).
    /// </summary>
    internal static bool LanceurGereCoreProxy(string chemin)
    {
        try
        {
            var fichier = new FileInfo(chemin);
            if (!fichier.Exists) return false;
            var connu = _lanceurConnu;
            if (connu is not null && connu.Chemin == fichier.FullName && connu.Taille == fichier.Length
                && connu.Date == fichier.LastWriteTimeUtc)
            {
                return connu.Gere;
            }

            var gere = PorteLaMarqueCoreProxy(File.ReadAllBytes(fichier.FullName));
            _lanceurConnu = new LanceurConnu(fichier.FullName, fichier.Length, fichier.LastWriteTimeUtc, gere);
            return gere;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static bool PorteLaMarqueCoreProxy(ReadOnlySpan<byte> exe) => exe.IndexOf(MarqueCoreProxy) >= 0;

    /// <summary>Ce qu'il faut faire d'un coeur dans le montage core_proxy.</summary>
    /// <param name="RemettreLeVrai">cores_real/ vers cores/ : le fichier de cores/ manque, ou c'est un wrapper.</param>
    /// <param name="RetirerLAncien">cores/ porte deja le vrai coeur (RetroBat l'a mis a jour) : la copie de cores_real/ est perimee.</param>
    /// <param name="Orphelin">Un wrapper dans cores/ et aucun vrai coeur nulle part : on n'y touche pas.</param>
    /// <param name="VraiDansCores">Une fois fait, cores/ porte le vrai coeur.</param>
    internal sealed record PlanCoreProxy(
        bool RemettreLeVrai, bool RetirerLAncien, bool Orphelin, bool VraiDansCores,
        bool VeutProxy, bool EcrireProxy, bool RetirerProxy);

    internal static PlanCoreProxy ArbitrerCoreProxy(
        bool dansCores, bool estWrapper, bool dansCoresReal, bool cible, bool exclu, bool proxyExiste, bool proxyAJour)
    {
        var remettre = dansCoresReal && (!dansCores || estWrapper);
        var retirer = dansCoresReal && dansCores && !estWrapper;
        var orphelin = dansCores && estWrapper && !dansCoresReal;
        var vrai = remettre || (dansCores && !estWrapper);
        var veut = vrai && cible && !exclu;
        return new PlanCoreProxy(remettre, retirer, orphelin, vrai, veut, veut && !proxyAJour, !veut && proxyExiste);
    }

    private static void ExecuterEnCoreProxy(
        string action, bool dryRun, string wrapperPath, string coresPath, string realCoresPath, string coreProxyPath,
        string backupRoot, WrapperReference reference, AuditCache cache,
        ApiExposeOptions.RetroArchWrapperDeploymentOptions options, RetroArchWrapperDeploymentResult result,
        CancellationToken cancellationToken)
    {
        var deployer = action.Equals("deploy", StringComparison.OrdinalIgnoreCase);
        // Tout ce que cores_real/ contient revient : le dossier est abandonne dans ce montage.
        var noms = Directory.EnumerateFiles(coresPath, "*.dll", SearchOption.TopDirectoryOnly)
            .Concat(Directory.Exists(realCoresPath)
                ? Directory.EnumerateFiles(realCoresPath, "*.dll", SearchOption.TopDirectoryOnly)
                : Enumerable.Empty<string>())
            .Select(Path.GetFileName).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(nom => nom, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var horodatage = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string? source = null;   // le wrapper, une fois dans core_proxy/ : chaque entree en devient un lien

        try
        {
            foreach (var nom in noms)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var corePath = Path.Combine(coresPath, nom);
                var realPath = Path.Combine(realCoresPath, nom);
                var proxyPath = Path.Combine(coreProxyPath, nom);
                var coreFile = new FileInfo(corePath);
                var estWrapper = coreFile.Exists && Inspecter(coreFile, reference, cache).IsWrapper;
                var proxy = new FileInfo(proxyPath);
                // Un lien ou une copie du wrapper de reference en a la taille et la date : rien a relire.
                // La date a deux secondes pres : FAT32 et exFAT arrondissent celle d'une copie.
                var proxyAJour = proxy.Exists && proxy.Length == reference.File.Length
                    && Math.Abs((proxy.LastWriteTimeUtc - reference.File.LastWriteTimeUtc).TotalSeconds) < 2;
                var exclu = EstExclu(nom, options.ExcludedCores);
                var cible = options.WrapAllCores || EstNomme(nom, options.TargetCores);
                var plan = ArbitrerCoreProxy(coreFile.Exists, estWrapper, File.Exists(realPath), cible, exclu, proxy.Exists, proxyAJour);

                if (plan.Orphelin)
                {
                    result.Warnings.Add($"{nom}: the wrapper is in cores but the real core is nowhere; left as is.");
                }

                var vraiEnPlace = plan.VraiDansCores;
                if (deployer)
                {
                    if (plan.RemettreLeVrai)
                    {
                        // Meme disque : un renommage, instantane quelle que soit la taille du coeur.
                        vraiEnPlace = Agir(result, nom, "move-real-core-back-to-cores", realPath, corePath, dryRun,
                            () => File.Move(realPath, corePath, overwrite: true));
                        if (vraiEnPlace && !dryRun) result.RealCoresMovedBack++;
                    }
                    else if (plan.RetirerLAncien)
                    {
                        var archive = Path.Combine(backupRoot, horodatage, "cores_real", nom);
                        if (Agir(result, nom, "retire-old-real-core", realPath, archive, dryRun, () =>
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
                                File.Move(realPath, archive, overwrite: true);
                            }) && !dryRun)
                        {
                            result.StaleRealCoresRetired++;
                        }
                    }

                    if (plan.EcrireProxy && vraiEnPlace)
                    {
                        var ecrit = Agir(result, nom, "link-wrapper-in-core-proxy", wrapperPath, proxyPath, dryRun, () =>
                        {
                            source ??= PreparerSource(wrapperPath, coreProxyPath);
                            EcrireEntree(source, proxyPath);
                        });
                        if (ecrit && !dryRun)
                        {
                            result.ProxyEntriesWritten++;
                            proxyAJour = true;
                        }
                    }
                    else if (plan.RetirerProxy)
                    {
                        if (Agir(result, nom, "remove-core-proxy-entry", proxyPath, string.Empty, dryRun,
                                () => File.Delete(proxyPath)) && !dryRun)
                        {
                            result.ProxyEntriesRemoved++;
                        }
                    }
                }

                var enveloppe = plan.VeutProxy && proxyAJour;
                result.Cores.Add(new RetroArchWrapperCoreStatus
                {
                    CoreName = nom,
                    CorePath = proxyPath,
                    RealCorePath = corePath,
                    IsWrapper = enveloppe,
                    HasRealCore = plan.VraiDansCores,
                    Excluded = exclu,
                    NeedsDeployment = plan.RemettreLeVrai || plan.RetirerLAncien || plan.EcrireProxy || plan.RetirerProxy,
                    NeedsRefresh = plan.EcrireProxy && proxy.Exists,
                    CoreBytes = proxy.Exists ? proxy.Length : 0,
                    RealCoreBytes = coreFile.Exists && !estWrapper ? coreFile.Length : null,
                    LastWriteTime = proxy.Exists ? proxy.LastWriteTime : default,
                    RealLastWriteTime = coreFile.Exists && !estWrapper ? coreFile.LastWriteTime : null,
                    Reason = plan.Orphelin ? "Wrapper in cores but the real core is missing from cores_real: left as is."
                        : exclu ? "Core excluded from wrapping; RetroArch loads it from cores."
                        : !cible ? "Not a target core; RetroArch loads it from cores."
                        : enveloppe ? "Real core in cores, wrapper in core_proxy."
                        : "Real core in cores; the wrapper will be linked in core_proxy."
                });
            }

            if (deployer && Directory.Exists(coreProxyPath))
            {
                // Les entrees d'un coeur qui n'existe plus, et les restes d'un passage interrompu.
                foreach (var fichier in Directory.EnumerateFiles(coreProxyPath).ToList())
                {
                    var nom = Path.GetFileName(fichier);
                    var enTrop = (EstUnResteDeSource(nom) && !string.Equals(fichier, source, StringComparison.OrdinalIgnoreCase))
                        || (nom.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !noms.Contains(nom, StringComparer.OrdinalIgnoreCase));
                    if (enTrop && Agir(result, nom, "remove-core-proxy-entry", fichier, string.Empty, dryRun,
                            () => File.Delete(fichier)) && !dryRun)
                    {
                        result.ProxyEntriesRemoved++;
                    }
                }
            }
        }
        finally
        {
            // Les liens gardent le fichier : le nom de passage peut partir.
            if (source is not null)
            {
                try { File.Delete(source); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        if (deployer && !dryRun)
        {
            SupprimerSiVide(realCoresPath);
            SupprimerSiVide(coreProxyPath);
        }

        result.CheckedCores = result.Cores.Count;
        result.ExcludedCores = result.Cores.Count(core => core.Excluded);
        result.WrappedCores = result.Cores.Count(core => core.IsWrapper);
        result.RealCores = result.Cores.Count(core => !core.IsWrapper);
        result.MissingRealCores = result.Cores.Count(core => !core.HasRealCore);
        result.PendingDeployments = result.Cores.Count(core => core.NeedsDeployment);
        result.StaleWrappers = result.Cores.Count(core => core.NeedsRefresh);
        result.DeployedCores = result.ProxyEntriesWritten;
    }

    /// <summary>
    /// L'ancien montage : un core_proxy/ laisse la (le lanceur ne le lit plus, ou la configuration
    /// force l'ancien montage) n'a plus de lecteur, il est retire.
    /// </summary>
    private static void RetirerCoreProxy(string coreProxyPath, bool dryRun, RetroArchWrapperDeploymentResult result)
    {
        if (!Directory.Exists(coreProxyPath)) return;
        foreach (var fichier in Directory.EnumerateFiles(coreProxyPath).ToList())
        {
            var nom = Path.GetFileName(fichier);
            if (!nom.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !EstUnResteDeSource(nom)) continue;
            if (Agir(result, nom, "remove-core-proxy-entry", fichier, string.Empty, dryRun, () => File.Delete(fichier)) && !dryRun)
            {
                result.ProxyEntriesRemoved++;
            }
        }

        if (!dryRun) SupprimerSiVide(coreProxyPath);
    }

    /// <summary>Note l'operation, et l'applique hors simulation. Faux si elle a echoue (fichier pris, droits).</summary>
    private static bool Agir(
        RetroArchWrapperDeploymentResult result, string nom, string operation, string source, string destination,
        bool dryRun, Action faire)
    {
        var trace = new RetroArchWrapperDeploymentAction
        {
            CoreName = nom,
            Operation = operation,
            SourcePath = source,
            DestinationPath = destination
        };
        result.Actions.Add(trace);
        if (dryRun) return true;
        try
        {
            faire();
            trace.Applied = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Warnings.Add($"{nom}: {operation} failed ({ex.Message}).");
            return false;
        }
    }

    private const string PrefixeSource = ".wrapper-";

    private static bool EstUnResteDeSource(string nom) =>
        nom.StartsWith(PrefixeSource, StringComparison.OrdinalIgnoreCase) && nom.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>Le wrapper copie une fois dans core_proxy/ : chaque entree en sera un lien.</summary>
    private static string PreparerSource(string wrapperPath, string coreProxyPath)
    {
        Directory.CreateDirectory(coreProxyPath);
        var source = Path.Combine(coreProxyPath, PrefixeSource + Guid.NewGuid().ToString("N") + ".tmp");
        // File.Copy garde la date du wrapper de reference : c'est elle qui dit qu'une entree est a jour.
        File.Copy(wrapperPath, source, overwrite: true);
        return source;
    }

    /// <summary>
    /// Une entree de core_proxy/ : un lien vers le wrapper, sans copie, ou une copie sur un disque qui
    /// ne connait pas les liens (FAT32, exFAT). L'ancienne entree part d'abord.
    /// </summary>
    private static void EcrireEntree(string source, string entree)
    {
        if (File.Exists(entree)) File.Delete(entree);
        if (!CreateHardLink(entree, source, IntPtr.Zero))
        {
            File.Copy(source, entree, overwrite: false);
        }
    }

    private static void SupprimerSiVide(string dossier)
    {
        try
        {
            if (Directory.Exists(dossier) && !Directory.EnumerateFileSystemEntries(dossier).Any())
            {
                Directory.Delete(dossier);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

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
                result.Layout,
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
