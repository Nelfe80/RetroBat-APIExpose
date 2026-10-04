using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class EsFeaturesMenuDeploymentService
{
    private static readonly JsonSerializerOptions LogJsonOptions = new() { WriteIndented = false };
    private static readonly Regex ApiExposeLocaleBlockRegex = new(
        @"(?:\r?\n)?# APIEXPOSE:BEGIN\r?\n.*?# APIEXPOSE:END(?:\r?\n)?",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private const string ApiExposeLocaleBeginMarker = "# APIEXPOSE:BEGIN";
    private const string ApiExposeLocaleEndMarker = "# APIEXPOSE:END";
    private readonly IOptionsMonitor<ApiExposeOptions> _options;
    private readonly EmulationStationSystemConfigService _systemConfig;
    private readonly ILogger<EsFeaturesMenuDeploymentService> _logger;
    private readonly RetroBat.Domain.Interfaces.IEsSettingsStore? _settings;

    public EsFeaturesMenuDeploymentService(
        IOptionsMonitor<ApiExposeOptions> options,
        EmulationStationSystemConfigService systemConfig,
        ILogger<EsFeaturesMenuDeploymentService> logger,
        RetroBat.Domain.Interfaces.IEsSettingsStore? settings = null)
    {
        _options = options;
        _systemConfig = systemConfig;
        _logger = logger;
        _settings = settings;
    }

    /// <summary>La langue d'ES (« Language » d'es_settings.cfg), vide si inconnue.</summary>
    public string LangueDES()
    {
        try
        {
            return _settings is not null && _settings.ReadAllSettings().TryGetValue("Language", out var langue) ? langue ?? string.Empty : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Nos traductions pour cette langue : resources/config-ESmenus/locales/&lt;langue&gt;/es-features.po.</summary>
    private static Dictionary<string, string> Traductions(string racine, string langue)
    {
        try
        {
            if (!Directory.Exists(racine)) return new Dictionary<string, string>();
            var dossiers = Directory.EnumerateDirectories(racine).Select(Path.GetFileName).OfType<string>();
            var dossier = EsFeaturesAnnexe.DossierDeLangue(dossiers, langue);
            if (dossier is null) return new Dictionary<string, string>();
            var po = Path.Combine(racine, dossier, "es-features.po");
            return File.Exists(po) ? EsFeaturesAnnexe.LirePo(File.ReadAllText(po)) : new Dictionary<string, string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>();
        }
    }

    public async Task<EsFeaturesMenuDeploymentResult> DeployAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue.EsFeaturesMenu;
        var featuresPath = ResolveFeaturesPath(options.FeaturesPath);
        var sourceFragmentPath = ResolvePluginPath(options.SourceFragmentPath);
        var localeSourceRoot = ResolvePluginPath(options.LocaleSourceRootPath);
        var localeTargetRoot = ResolveLocaleTargetRoot(options.LocaleTargetRootPath);
        var backupRoot = ResolvePluginPath(options.BackupPath);
        var logPath = ResolvePluginPath(options.LogFilePath);

        var result = new EsFeaturesMenuDeploymentResult
        {
            Enabled = options.Enabled,
            DryRun = dryRun,
            FeaturesPath = featuresPath,
            SourceFragmentPath = sourceFragmentPath,
            LocaleSourceRootPath = localeSourceRoot,
            LocaleTargetRootPath = localeTargetRoot,
            BackupRootPath = backupRoot
        };

        try
        {
            if (!options.Enabled)
            {
                result.Warnings.Add("ES features menu deployment is disabled in appsettings.");
                return result;
            }

            // 1. LE FICHIER DE RETROBAT : on n'y ajoute plus rien (2026-10-04). On retire ce que les
            // versions precedentes y avaient mis ; une fois fait, ce passage ne change plus rien.
            XElement? copieRetroBat = null;
            if (File.Exists(featuresPath))
            {
                var document = XDocument.Load(featuresPath, LoadOptions.PreserveWhitespace);
                var root = document.Root;
                if (root == null || !string.Equals(root.Name.LocalName, "features", StringComparison.OrdinalIgnoreCase))
                {
                    result.Warnings.Add("es_features.cfg root element is not <features>.");
                }
                else
                {
                    if (root.Element("sharedFeatures") is { } anciennesOptions)
                    {
                        result.RemovedSharedFeatureCount = RemoveApiExposeSharedFeatureDefinitions(anciennesOptions);
                    }
                    if (root.Element("globalFeatures") is { } anciennesPlaces)
                    {
                        result.RemovedGlobalFeatureCount = RemoveApiExposeGlobalMenuEntries(anciennesPlaces);
                    }
                    result.RemovedSystemPanelFeatureCount = RemoveApiExposePanelSystemFeatures(root);

                    var cleanedXml = SerializeDocument(document);
                    var currentXml = await File.ReadAllTextAsync(featuresPath, cancellationToken);
                    result.Changed = !string.Equals(NormalizeLineEndings(currentXml), NormalizeLineEndings(cleanedXml), StringComparison.Ordinal);
                    if (result.Changed && !dryRun)
                    {
                        if (options.BackupEnabled)
                        {
                            Directory.CreateDirectory(backupRoot);
                            var backupPath = Path.Combine(backupRoot, $"es_features.cfg.{DateTime.Now:yyyyMMdd-HHmmss-fff}.nettoyage.bak");
                            File.Copy(featuresPath, backupPath, overwrite: true);
                            result.BackupPath = backupPath;
                            EnforceBackupRetention(backupRoot, options.BackupRetentionCount);
                        }

                        var tempPath = featuresPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        await File.WriteAllTextAsync(tempPath, cleanedXml, cancellationToken);
                        File.Replace(tempPath, featuresPath, null, ignoreMetadataErrors: true);
                        _logger.LogInformation("ES features : anciens ajouts d'APIExpose retires de es_features.cfg (ils vivent desormais dans {Annexe}).", EsFeaturesAnnexe.NomDuFichier);
                    }

                    // La copie propre sert a placer les options du panel comme avant.
                    copieRetroBat = new XElement(root);
                }
            }
            else
            {
                result.Warnings.Add($"es_features.cfg not found: {featuresPath}");
            }

            // Les anciennes traductions, recopiees dans les .po de RetroBat : retirees elles aussi. Un
            // echec ici n'empeche pas d'ecrire notre fichier.
            if (options.LocaleDeploymentEnabled)
            {
                try
                {
                    await RemoveLocaleBlocksAsync(localeTargetRoot, backupRoot, options, dryRun, result, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.Warnings.Add(ex.Message);
                    _logger.LogWarning(ex, "ES features : anciennes traductions non retirees des .po de RetroBat, nouvel essai au prochain demarrage.");
                }
            }

            // 2. NOTRE FICHIER, es_features_apiexpose.cfg, a cote de es_features.cfg.
            var fragment = LoadFragment(sourceFragmentPath, result);
            var features = fragment.Features.Count > 0
                ? fragment.Features
                : new List<XElement> { CreateDefaultFeature() };
            var sharedFeatureEntries = fragment.SharedFeatures.Count > 0
                ? fragment.SharedFeatures
                : new List<XElement> { CreateDefaultSharedFeature() };
            if (copieRetroBat is not null)
            {
                result.InstalledSystemPanelFeatureCount = AppendApiExposePanelSystemFeatures(copieRetroBat, _systemConfig);
            }

            var langue = LangueDES();
            var traductions = options.LocaleDeploymentEnabled ? Traductions(localeSourceRoot, langue) : new Dictionary<string, string>();
            var annexe = EsFeaturesAnnexe.Construire(
                features.Select(NormalizeFeatureElement),
                sharedFeatureEntries.Select(NormalizeSharedFeatureElement),
                copieRetroBat,
                IsApiExposePanelValue,
                traductions);
            var annexeXml = EsFeaturesAnnexe.Serialiser(annexe);
            var annexePath = Path.Combine(Path.GetDirectoryName(featuresPath) ?? RetroBatPaths.EmulationStationConfigRoot, EsFeaturesAnnexe.NomDuFichier);
            var annexeActuelle = File.Exists(annexePath) ? await File.ReadAllTextAsync(annexePath, cancellationToken) : null;
            var annexeChangee = !string.Equals(annexeActuelle, annexeXml, StringComparison.Ordinal);
            if (annexeChangee && !dryRun)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(annexePath)!);
                var tempAnnexe = annexePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(tempAnnexe, annexeXml, new UTF8Encoding(false), cancellationToken);
                File.Move(tempAnnexe, annexePath, overwrite: true);
                _logger.LogInformation("ES features : {Annexe} ecrit ({Options} options, {Panels} options de panel, langue {Langue}).",
                    EsFeaturesAnnexe.NomDuFichier, features.Count, result.InstalledSystemPanelFeatureCount, string.IsNullOrEmpty(langue) ? "?" : langue);
            }

            result.Changed |= annexeChangee;
            result.LocaleChanged |= annexeChangee && traductions.Count > 0;
            result.Installed = true;
            result.InstalledFeatureCount = features.Count;
            result.InstalledMenuEntryCount = sharedFeatureEntries.Count;
            result.InstalledLocaleCount = traductions.Count > 0 ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException)
        {
            result.Warnings.Add(ex.Message);
            _logger.LogWarning(ex, "ES features menu deployment failed.");
        }
        finally
        {
            await WriteLogAsync(logPath, options, result, cancellationToken);
        }

        return result;
    }

    public async Task<EsFeaturesMenuDeploymentResult> RemoveAsync(bool dryRun, CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue.EsFeaturesMenu;
        var featuresPath = ResolveFeaturesPath(options.FeaturesPath);
        var localeTargetRoot = ResolveLocaleTargetRoot(options.LocaleTargetRootPath);
        var backupRoot = ResolvePluginPath(options.BackupPath);
        var logPath = ResolvePluginPath(options.LogFilePath);

        var result = new EsFeaturesMenuDeploymentResult
        {
            Enabled = options.Enabled,
            DryRun = dryRun,
            FeaturesPath = featuresPath,
            SourceFragmentPath = ResolvePluginPath(options.SourceFragmentPath),
            LocaleSourceRootPath = ResolvePluginPath(options.LocaleSourceRootPath),
            LocaleTargetRootPath = localeTargetRoot,
            BackupRootPath = backupRoot,
            Operation = "remove"
        };

        try
        {
            if (!options.Enabled)
            {
                result.Warnings.Add("ES features menu deployment is disabled in appsettings.");
                return result;
            }

            if (!File.Exists(featuresPath))
            {
                result.Warnings.Add($"es_features.cfg not found: {featuresPath}");
                return result;
            }

            var document = XDocument.Load(featuresPath, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root == null || !string.Equals(root.Name.LocalName, "features", StringComparison.OrdinalIgnoreCase))
            {
                result.Warnings.Add("es_features.cfg root element is not <features>.");
                return result;
            }

            var sharedFeatures = root.Element("sharedFeatures");
            var globalFeatures = root.Element("globalFeatures");
            if (sharedFeatures == null || globalFeatures == null)
            {
                result.Warnings.Add("es_features.cfg must contain <sharedFeatures> and <globalFeatures>.");
                return result;
            }

            result.RemovedSharedFeatureCount = RemoveApiExposeSharedFeatureDefinitions(sharedFeatures);
            result.RemovedGlobalFeatureCount = RemoveApiExposeGlobalMenuEntries(globalFeatures);
            result.RemovedSystemPanelFeatureCount = RemoveApiExposePanelSystemFeatures(root);

            var updatedXml = SerializeDocument(document);
            var currentXml = await File.ReadAllTextAsync(featuresPath, cancellationToken);
            result.Changed = !string.Equals(NormalizeLineEndings(currentXml), NormalizeLineEndings(updatedXml), StringComparison.Ordinal);

            if (result.Changed && !dryRun)
            {
                if (options.BackupEnabled)
                {
                    Directory.CreateDirectory(backupRoot);
                    var backupPath = Path.Combine(
                        backupRoot,
                        $"es_features.cfg.{DateTime.Now:yyyyMMdd-HHmmss}.remove.bak");
                    File.Copy(featuresPath, backupPath, overwrite: false);
                    result.BackupPath = backupPath;
                    EnforceBackupRetention(backupRoot, options.BackupRetentionCount);
                }

                var tempPath = featuresPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllTextAsync(tempPath, updatedXml, cancellationToken);
                File.Replace(tempPath, featuresPath, null, ignoreMetadataErrors: true);
            }

            if (options.LocaleDeploymentEnabled)
            {
                await RemoveLocaleBlocksAsync(localeTargetRoot, backupRoot, options, dryRun, result, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException)
        {
            result.Warnings.Add(ex.Message);
            _logger.LogWarning(ex, "ES features menu removal failed.");
        }
        finally
        {
            await WriteLogAsync(logPath, options, result, cancellationToken);
        }

        return result;
    }

    private static async Task RemoveLocaleBlocksAsync(
        string targetRoot,
        string backupRoot,
        ApiExposeOptions.EsFeaturesMenuOptions options,
        bool dryRun,
        EsFeaturesMenuDeploymentResult result,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(targetRoot))
        {
            return;
        }

        foreach (var targetPath in Directory.EnumerateFiles(targetRoot, "es-features.po", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = await File.ReadAllTextAsync(targetPath, cancellationToken);
            var updated = RemoveApiExposeLocaleBlock(current);
            if (string.Equals(NormalizeLineEndings(current), NormalizeLineEndings(updated), StringComparison.Ordinal))
            {
                continue;
            }

            result.LocaleChanged = true;
            result.RemovedLocaleCount++;
            if (dryRun)
            {
                continue;
            }

            BackupFile(targetPath, backupRoot, ".locale.remove.bak", options);
            await File.WriteAllTextAsync(targetPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken);
        }
    }

    private static string RemoveApiExposeLocaleBlock(string poContent)
    {
        return ApiExposeLocaleBlockRegex.Replace(poContent ?? string.Empty, Environment.NewLine).TrimEnd() + Environment.NewLine;
    }

    public void PrepareLogFilesOnStartup()
    {
        var options = _options.CurrentValue.EsFeaturesMenu;
        var logPath = ResolvePluginPath(options.LogFilePath);

        if (options.ResetLogOnStartup && !string.IsNullOrWhiteSpace(logPath))
        {
            try
            {
                var logDirectory = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrWhiteSpace(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                File.WriteAllText(logPath, string.Empty, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not reset ES features deployment log.");
            }
        }

        if (options.BackupEnabled)
        {
            EnforceBackupRetention(ResolvePluginPath(options.BackupPath), options.BackupRetentionCount);
        }
    }

    private static void BackupFile(
        string targetPath,
        string backupRoot,
        string suffix,
        ApiExposeOptions.EsFeaturesMenuOptions options)
    {
        if (!options.BackupEnabled)
        {
            return;
        }

        Directory.CreateDirectory(backupRoot);
        var safeName = Regex.Replace(Path.GetFileName(targetPath), @"[^A-Za-z0-9_.-]+", "_");
        // La LANGUE dans le nom : tous les .po s'appellent es-features.po, et deux langues traitees
        // dans la meme seconde donnaient le meme nom ; la copie echouait et arretait tout
        // (« ...already exists », vu chez un testeur et sur la borne de dev le 2026-10-04).
        var dossier = Regex.Replace(new DirectoryInfo(Path.GetDirectoryName(targetPath) ?? string.Empty).Name, @"[^A-Za-z0-9_.-]+", "_");
        var backupPath = Path.Combine(backupRoot, $"{dossier}.{safeName}.{DateTime.Now:yyyyMMdd-HHmmss-fff}{suffix}");
        File.Copy(targetPath, backupPath, overwrite: true);
        EnforceBackupRetention(backupRoot, options.BackupRetentionCount);
    }

    private static void EnforceBackupRetention(string backupRoot, int retentionCount)
    {
        if (retentionCount < 0 || string.IsNullOrWhiteSpace(backupRoot) || !Directory.Exists(backupRoot))
        {
            return;
        }

        var files = Directory.EnumerateFiles(backupRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files.Skip(retentionCount))
        {
            try
            {
                file.Delete();
            }
            catch
            {
                // Best effort cleanup: backups must never block menu deployment.
            }
        }
    }

    private static MenuFragment LoadFragment(string sourceFragmentPath, EsFeaturesMenuDeploymentResult result)
    {
        if (!File.Exists(sourceFragmentPath))
        {
            result.Warnings.Add($"Menu fragment not found, using built-in minimal fallback: {sourceFragmentPath}");
            return new MenuFragment(new List<XElement>(), new List<XElement>());
        }

        try
        {
            var content = File.ReadAllText(sourceFragmentPath);
            var wrapper = XDocument.Parse("<root>" + content + "</root>", LoadOptions.PreserveWhitespace);
            var features = wrapper.Root?.Elements("feature")
                .Where(element => IsApiExposeValue(element.Attribute("value")?.Value))
                .Select(element => new XElement(element))
                .ToList()
                ?? new List<XElement>();
            var sharedFeatures = wrapper.Root?.Elements("sharedFeature")
                .Where(element => IsApiExposeValue(element.Attribute("value")?.Value))
                .Select(element => new XElement(element))
                .ToList()
                ?? new List<XElement>();

            return new MenuFragment(features, sharedFeatures);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            result.Warnings.Add($"Menu fragment unreadable, using built-in minimal fallback: {ex.Message}");
            return new MenuFragment(new List<XElement>(), new List<XElement>());
        }
    }

    private static int RemoveApiExposeSharedFeatureDefinitions(XElement sharedFeatures)
    {
        var nodes = sharedFeatures.Elements("feature")
            .Where(element => IsApiExposeValue(element.Attribute("value")?.Value))
            .ToList();

        foreach (var node in nodes)
        {
            node.Remove();
        }

        RemoveApiExposeMarkerComments(sharedFeatures);
        return nodes.Count;
    }

    private static int RemoveApiExposeGlobalMenuEntries(XElement globalFeatures)
    {
        var nodes = globalFeatures.Elements()
            .Where(element =>
                IsApiExposeValue(element.Attribute("value")?.Value) ||
                IsLegacyApiExposeGroup(element.Attribute("group")?.Value))
            .ToList();

        foreach (var node in nodes)
        {
            node.Remove();
        }

        RemoveApiExposeMarkerComments(globalFeatures);
        return nodes.Count;
    }

    private static void RemoveApiExposeMarkerComments(XElement parent)
    {
        foreach (var comment in parent.Nodes().OfType<XComment>()
            .Where(comment => comment.Value.Contains("APIEXPOSE", StringComparison.OrdinalIgnoreCase))
            .ToList())
        {
            comment.Remove();
        }
    }

    private static int RemoveApiExposePanelSystemFeatures(XElement root)
    {
        foreach (var systemNode in root.Descendants("system")
            .Where(system => system.Nodes()
                .OfType<XComment>()
                .Any(comment => comment.Value.Contains("APIEXPOSE:PANEL:SYSTEM", StringComparison.OrdinalIgnoreCase)))
            .ToList())
        {
            systemNode.Remove();
        }

        var nodes = root.Descendants("system")
            .Elements("feature")
            .Where(element => IsApiExposePanelValue(element.Attribute("value")?.Value))
            .ToList();

        foreach (var node in nodes)
        {
            node.Remove();
        }

        foreach (var comment in root.Descendants("system")
            .Nodes()
            .OfType<XComment>()
            .Where(comment => comment.Value.Contains("APIEXPOSE:PANEL", StringComparison.OrdinalIgnoreCase))
            .ToList())
        {
            comment.Remove();
        }

        return nodes.Count;
    }

    private static int AppendApiExposePanelSystemFeatures(
        XElement root,
        EmulationStationSystemConfigService systemConfig)
    {
        var definitions = BuildApiExposePanelSystemFeatures();
        if (definitions.Count == 0)
        {
            return 0;
        }

        var installed = 0;
        foreach (var systemNode in root.Descendants("system").ToList())
        {
            var systemNames = SplitSystemNames(systemNode.Attribute("name")?.Value).ToList();
            var matchedSystemIds = systemNames
                .Where(name => definitions.ContainsKey(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (matchedSystemIds.Count == 0)
            {
                continue;
            }

            if (systemNames.Count == 1 && matchedSystemIds.Count == 1)
            {
                AppendApiExposePanelFeature(systemNode, definitions[matchedSystemIds[0]]);
                installed++;
                continue;
            }

            foreach (var systemId in matchedSystemIds)
            {
                AddDedicatedApiExposePanelSystemNodeAfter(systemNode, systemId, definitions[systemId]);
                installed++;
            }
        }

        installed += AppendApiExposePanelCoreScopedFeatures(root, definitions, systemConfig);
        return installed;
    }

    private static int AppendApiExposePanelCoreScopedFeatures(
        XElement root,
        IReadOnlyDictionary<string, XElement> definitions,
        EmulationStationSystemConfigService systemConfig)
    {
        var installed = 0;
        foreach (var emulatorNode in root.Elements("emulator").ToList())
        {
            var emulatorNames = SplitSystemNames(emulatorNode.Attribute("name")?.Value).ToList();
            if (emulatorNames.Count == 0)
            {
                continue;
            }

            foreach (var coreNode in emulatorNode.Descendants("core").ToList())
            {
                var coreName = (coreNode.Attribute("name")?.Value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(coreName))
                {
                    continue;
                }

                foreach (var (systemId, feature) in definitions)
                {
                    if (!CoreCanRunSystem(systemConfig, systemId, emulatorNames, coreName) ||
                        CoreAlreadyHasSystemFeature(coreNode, systemId))
                    {
                        continue;
                    }

                    AppendDedicatedApiExposePanelSystemNode(coreNode, systemId, feature);
                    installed++;
                }
            }
        }

        return installed;
    }

    private static void AddDedicatedApiExposePanelSystemNodeAfter(XElement systemNode, string systemId, XElement feature)
    {
        systemNode.AddAfterSelf(new XText(Environment.NewLine + "    "));
        systemNode.AddAfterSelf(CreateDedicatedApiExposePanelSystemNode(systemId, feature));
    }

    private static void AppendDedicatedApiExposePanelSystemNode(XElement parentNode, string systemId, XElement feature)
    {
        parentNode.Add(new XText(Environment.NewLine + "      "));
        parentNode.Add(CreateDedicatedApiExposePanelSystemNode(systemId, feature));
        parentNode.Add(new XText(Environment.NewLine + "    "));
    }

    private static XElement CreateDedicatedApiExposePanelSystemNode(string systemId, XElement feature)
    {
        return new XElement(
            "system",
            new XAttribute("name", systemId),
            new XText(Environment.NewLine + "        "),
            new XComment(" APIEXPOSE:PANEL:SYSTEM "),
            new XText(Environment.NewLine + "        "),
            new XElement(feature),
            new XText(Environment.NewLine + "      "));
    }

    private static bool CoreCanRunSystem(
        EmulationStationSystemConfigService systemConfig,
        string systemId,
        IReadOnlyList<string> emulatorNames,
        string coreName)
    {
        return systemConfig.GetEmulatorCores(systemId).Any(entry =>
            emulatorNames.Any(emulatorName => string.Equals(entry.Emulator, emulatorName, StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(entry.Core, coreName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool CoreAlreadyHasSystemFeature(XElement coreNode, string systemId)
    {
        return coreNode.Elements("system")
            .SelectMany(system => SplitSystemNames(system.Attribute("name")?.Value))
            .Any(name => string.Equals(name, systemId, StringComparison.OrdinalIgnoreCase));
    }

    private static void AppendApiExposePanelFeature(XElement systemNode, XElement feature)
    {
        systemNode.Add(new XText(Environment.NewLine + "        "));
        systemNode.Add(new XComment(" APIEXPOSE:PANEL "));
        systemNode.Add(new XText(Environment.NewLine + "        "));
        systemNode.Add(new XElement(feature));
        systemNode.Add(new XText(Environment.NewLine + "      "));
    }

    private static Dictionary<string, XElement> BuildApiExposePanelSystemFeatures()
    {
        var result = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(RetroBatPaths.DynPanelsSystemsRoot))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(RetroBatPaths.DynPanelsSystemsRoot, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
                if (root == null)
                {
                    continue;
                }

                var systemId = NormalizeSystemId(root["system"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(file));
                if (string.IsNullOrWhiteSpace(systemId))
                {
                    continue;
                }

                var layouts = root["system_template"]?["layouts"] as JsonObject;
                if (layouts == null)
                {
                    continue;
                }

                var availableLayouts = layouts
                    .Select(layout => layout.Key)
                    .Where(IsNamedPanelLayout)
                    .OrderBy(layout => layout, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (availableLayouts.Count == 0)
                {
                    continue;
                }

                result[systemId] = CreateApiExposePanelFeature(systemId, availableLayouts);
            }
            catch
            {
                // Ignore malformed dynpanels definitions; the generated menu should remain deployable.
            }
        }

        return result;
    }

    private static XElement CreateApiExposePanelFeature(string systemId, IReadOnlyList<string> layouts)
    {
        var feature = new XElement(
            "feature",
            new XAttribute("name", "CONTROL PANEL"),
            new XAttribute("group", "ADVANCED SETTINGS"),
            new XAttribute("value", $"apiexpose_panel_{systemId}"),
            new XAttribute("description", "Choose the panel layout used for this system. AUTO uses the cabinet button count."),
            new XAttribute("order", "510"));

        feature.Add(new XText(Environment.NewLine + "          "));
        feature.Add(new XElement("choice", new XAttribute("name", "AUTO"), new XAttribute("value", "auto")));
        foreach (var layout in layouts)
        {
            feature.Add(new XText(Environment.NewLine + "          "));
            feature.Add(new XElement(
                "choice",
                new XAttribute("name", BuildPanelChoiceName(layout)),
                new XAttribute("value", layout)));
        }

        feature.Add(new XText(Environment.NewLine + "        "));
        return feature;
    }

    private static string BuildPanelChoiceName(string layout)
    {
        var name = layout.Contains(':', StringComparison.Ordinal)
            ? layout.Split(':', 2)[1]
            : layout;
        return Regex.Replace(name, @"\s+", " ").Trim().ToUpperInvariant();
    }

    private static bool IsNamedPanelLayout(string layout)
    {
        return !string.IsNullOrWhiteSpace(layout) &&
            layout.Contains(':', StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(layout.Split(':', 2)[1]);
    }

    private static string NormalizeSystemId(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static IEnumerable<string> SplitSystemNames(string? value)
    {
        return (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSystemId)
            .Where(name => !string.IsNullOrWhiteSpace(name));
    }

    private static void AppendSharedFeatureBlock(XElement sharedFeatures, IReadOnlyList<XElement> features)
    {
        sharedFeatures.Add(new XText(Environment.NewLine + "    "));
        sharedFeatures.Add(new XComment(" APIEXPOSE:BEGIN:SHARED "));
        foreach (var feature in features)
        {
            sharedFeatures.Add(new XText(Environment.NewLine + "    "));
            sharedFeatures.Add(NormalizeFeatureElement(feature));
        }
        sharedFeatures.Add(new XText(Environment.NewLine + "    "));
        sharedFeatures.Add(new XComment(" APIEXPOSE:END:SHARED "));
        sharedFeatures.Add(new XText(Environment.NewLine + "  "));
    }

    private static void AppendGlobalFeatureBlock(XElement globalFeatures, IReadOnlyList<XElement> sharedFeatures)
    {
        globalFeatures.Add(new XText(Environment.NewLine + "    "));
        globalFeatures.Add(new XComment(" APIEXPOSE:BEGIN:GLOBAL "));
        foreach (var sharedFeature in sharedFeatures)
        {
            globalFeatures.Add(new XText(Environment.NewLine + "    "));
            globalFeatures.Add(NormalizeSharedFeatureElement(sharedFeature));
        }
        globalFeatures.Add(new XText(Environment.NewLine + "    "));
        globalFeatures.Add(new XComment(" APIEXPOSE:END:GLOBAL "));
        globalFeatures.Add(new XText(Environment.NewLine + "  "));
    }

    private static XElement NormalizeFeatureElement(XElement feature)
    {
        var normalized = new XElement(
            "feature",
            new XAttribute("name", feature.Attribute("name")?.Value ?? "ENABLE API EXPOSE"),
            new XAttribute("value", NormalizeApiExposeValue(feature.Attribute("value")?.Value, "global.apiexpose.enabled")));

        var description = feature.Attribute("description")?.Value;
        if (!string.IsNullOrWhiteSpace(description))
        {
            normalized.SetAttributeValue("description", description);
        }

        foreach (var attribute in feature.Attributes())
        {
            var name = attribute.Name.LocalName;
            if (string.Equals(name, "name", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "value", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "description", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            normalized.SetAttributeValue(attribute.Name, attribute.Value);
        }

        foreach (var child in feature.Elements())
        {
            normalized.Add(new XText(Environment.NewLine + "      "));
            normalized.Add(new XElement(child));
        }

        if (feature.Elements().Any())
        {
            normalized.Add(new XText(Environment.NewLine + "    "));
        }

        return normalized;
    }

    private static XElement NormalizeSharedFeatureElement(XElement sharedFeature)
    {
        return new XElement(
            "sharedFeature",
            new XAttribute("group", sharedFeature.Attribute("group")?.Value ?? "EXTENDED OPTIONS"),
            new XAttribute("submenu", sharedFeature.Attribute("submenu")?.Value ?? string.Empty),
            new XAttribute("value", NormalizeApiExposeValue(sharedFeature.Attribute("value")?.Value, "global.apiexpose.enabled")),
            new XAttribute("order", sharedFeature.Attribute("order")?.Value ?? "900"));
    }

    private static XElement CreateDefaultFeature()
    {
        return new XElement(
            "feature",
            new XAttribute("name", "ENABLE API EXPOSE"),
            new XAttribute("value", "global.apiexpose.enabled"),
            new XAttribute("description", "Enables or disables the advanced features of the local plugin."),
            new XAttribute("preset", "switch"));
    }

    private static XElement CreateDefaultSharedFeature()
    {
        return new XElement(
            "sharedFeature",
            new XAttribute("group", "EXTENDED OPTIONS"),
            new XAttribute("submenu", string.Empty),
            new XAttribute("value", "global.apiexpose.enabled"),
            new XAttribute("order", "900"));
    }

    private static bool IsApiExposeValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.Trim().StartsWith("global.apiexpose.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsApiExposePanelValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            value.Trim().StartsWith("apiexpose_panel", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeApiExposeValue(string? value, string fallback)
    {
        return IsApiExposeValue(value)
            ? value!.Trim()
            : fallback;
    }

    private static bool IsLegacyApiExposeGroup(string? value)
    {
        return string.Equals(value?.Trim(), "APIEXPOSE", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveFeaturesPath(string configuredPath)
    {
        return string.IsNullOrWhiteSpace(configuredPath)
            ? RetroBatPaths.EmulationStationFeaturesPath
            : ResolvePath(configuredPath);
    }

    private static string ResolvePluginPath(string configuredPath)
    {
        return string.IsNullOrWhiteSpace(configuredPath)
            ? RetroBatPaths.PluginRoot
            : ResolvePath(configuredPath);
    }

    private static string ResolveLocaleTargetRoot(string configuredPath)
    {
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(RetroBatPaths.RetroBatRoot, "emulationstation", "es_features.locale")
            : ResolvePath(configuredPath);
    }

    private static string ResolvePath(string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? Path.GetFullPath(configuredPath)
            : Path.GetFullPath(Path.Combine(RetroBatPaths.PluginRoot, configuredPath));
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string SerializeDocument(XDocument document)
    {
        var body = document.ToString(SaveOptions.DisableFormatting);
        var xml = document.Declaration == null
            ? body
            : document.Declaration + Environment.NewLine + body;

        return NormalizeXmlBlankLines(xml);
    }

    private static string NormalizeXmlBlankLines(string xml)
    {
        var lines = Regex.Split(xml, "\r\n|\n|\r");
        return string.Join(
            Environment.NewLine,
            lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static async Task WriteLogAsync(
        string logPath,
        ApiExposeOptions.EsFeaturesMenuOptions options,
        EsFeaturesMenuDeploymentResult result,
        CancellationToken cancellationToken)
    {
        if (!options.LogEnabled)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var line = JsonSerializer.Serialize(result, LogJsonOptions);
        await File.AppendAllTextAsync(logPath, line + Environment.NewLine, cancellationToken);
    }

    private sealed record MenuFragment(IReadOnlyList<XElement> Features, IReadOnlyList<XElement> SharedFeatures);
}

public sealed class EsFeaturesMenuDeploymentResult
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public string Operation { get; set; } = "deploy";
    public bool Enabled { get; set; }
    public bool DryRun { get; set; }
    public bool Changed { get; set; }
    public bool Installed { get; set; }
    public int InstalledFeatureCount { get; set; }
    public int InstalledMenuEntryCount { get; set; }
    public int RemovedSharedFeatureCount { get; set; }
    public int RemovedGlobalFeatureCount { get; set; }
    public int RemovedSystemPanelFeatureCount { get; set; }
    public int InstalledSystemPanelFeatureCount { get; set; }
    public bool LocaleChanged { get; set; }
    public int InstalledLocaleCount { get; set; }
    public int RemovedLocaleCount { get; set; }
    public string FeaturesPath { get; set; } = string.Empty;
    public string SourceFragmentPath { get; set; } = string.Empty;
    public string LocaleSourceRootPath { get; set; } = string.Empty;
    public string LocaleTargetRootPath { get; set; } = string.Empty;
    public string BackupRootPath { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = new();
}
