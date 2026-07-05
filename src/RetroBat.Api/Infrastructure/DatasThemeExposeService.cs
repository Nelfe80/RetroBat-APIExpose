using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;
using RetroBat.Providers.EmulationStation;

namespace RetroBat.Api.Infrastructure;

public sealed class DatasThemeExposeService
{
    private const string ViewName = "gamecarousel, detailed, grid";
    private static readonly Regex HiscoreLineNameRegex = new("^hiscoreline\\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ButtonTextNameRegex = new("^(button\\d+|p\\d+button\\d+|buttonstart|buttoncoin|buttonselect|panel_layout|control\\d*|panel_joystick|panel_joystick_color)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] KnownDeviceTypes =
    {
        "unknown",
        "joy2wayhorizontal",
        "joy2wayvertical",
        "joy4way",
        "joy8way",
        "double_joystick",
        "double_joystick_4way",
        "double_joystick_8way",
        "double_joystick_2way_vertical",
        "triggerstick",
        "top_fire_joystick",
        "rotary_joystick",
        "mechanical_rotary_joystick",
        "optical_rotary_joystick",
        "trackball",
        "spinner",
        "dial",
        "vertical_dial",
        "paddle",
        "vertical_paddle",
        "wheel",
        "pedal",
        "pedal2",
        "pedal3",
        "analog_stick",
        "yoke",
        "throttle",
        "shifter",
        "turntable",
        "roller",
        "handlebar",
        "gun",
        "lightgun",
        "mahjong_panel",
        "hanafuda_panel",
        "keypad",
        "gambling_panel",
        "poker_panel",
        "slot_panel",
        "misc",
        "only_buttons"
    };

    private readonly PanelsCatalogService _panels;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;
    private readonly ILogger<DatasThemeExposeService>? _logger;
    private readonly object _verticalRomCacheLock = new();
    private HashSet<string>? _arcadeVerticalRoms;
    private static readonly string[] ArcadeLikeSystems = { "arcade", "mame", "fbneo", "neogeo" };

    public DatasThemeExposeService(
        PanelsCatalogService panels,
        ApiExposeRuntimeOptionsService runtimeOptions,
        ILogger<DatasThemeExposeService>? logger = null)
    {
        _panels = panels;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
    }

    public async Task<DatasThemeExportResult> ExportAllAsync(CancellationToken cancellationToken = default)
    {
        var result = new DatasThemeExportResult
        {
            Enabled = _runtimeOptions.IsDatasThemeExposeEnabled(),
            CpoEnabled = _runtimeOptions.IsCpoControlPanelExposeEnabled(),
            HighScoreEnabled = _runtimeOptions.IsHighScoreExposeEnabled()
        };

        if (!result.Enabled)
        {
            result.Message = "Themes Manager disabled.";
            return result;
        }

        var systemEntries = _panels.ListPanels("system");
        foreach (var entry in systemEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.SystemId))
            {
                continue;
            }

            var exported = await ExportPanelAsync(entry.SystemId, null, cancellationToken);
            result.SystemFilesScanned++;
            if (exported.Written)
            {
                result.FilesChanged++;
            }
        }

        foreach (var entry in EnumerateExportableGameEntries(systemEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.SystemId) || string.IsNullOrWhiteSpace(entry.Rom))
            {
                continue;
            }

            var exported = await ExportPanelAsync(entry.SystemId, entry.Rom, cancellationToken);
            result.GameFilesScanned++;
            if (exported.Written)
            {
                result.FilesChanged++;
            }
        }

        if (result.HighScoreEnabled)
        {
            var imported = await ImportLegacyHiscoreFilesAsync(cancellationToken);
            result.LegacyHiscoreFilesScanned = imported.Scanned;
            result.LegacyHiscoreFilesChanged = imported.Changed;
            result.FilesChanged += imported.Changed;

            var migrated = await MigrateLegacyArcadeGameInfosAsync(cancellationToken);
            result.LegacyGameInfosScanned = migrated.Scanned;
            result.LegacyGameInfosChanged = migrated.Changed;
            result.FilesChanged += migrated.Changed;
        }

        var markers = await ExportArcadeThemeMarkersAsync(cancellationToken);
        result.VerticalMarkersScanned = markers.Scanned;
        result.VerticalMarkersChanged = markers.Changed;
        result.FilesChanged += markers.Changed;

        var removed = CleanupRedundantGameInfoFiles();
        result.RedundantFilesRemoved = removed;
        result.FilesChanged += removed;

        result.Message = $"Themes Manager export completed. Changed={result.FilesChanged}.";
        return result;
    }

    public async Task<DatasThemeFileResult> ExportCurrentAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _panels.GetCurrentSnapshot();
        return await ExportPanelAsync(snapshot.SystemId, snapshot.Rom, cancellationToken);
    }

    public async Task<DatasThemeFileResult> ExportAsync(string systemId, string? rom, CancellationToken cancellationToken = default)
    {
        return await ExportPanelAsync(systemId, rom, cancellationToken);
    }

    public string GetCurrentThemeXml()
    {
        var snapshot = _panels.GetCurrentSnapshot();
        var layout = ResolveSnapshotWithConfiguredLayout(snapshot.SystemId, snapshot.Rom, snapshot.Core);
        return BuildPreviewXml(layout);
    }

    public string GetThemeXml(string systemId, string? rom, string? core = null)
    {
        var layout = ResolveSnapshotWithConfiguredLayout(systemId, rom, core);
        return BuildPreviewXml(layout);
    }

    public DatasThemeAuditResult Audit()
    {
        var result = new DatasThemeAuditResult
        {
            RuntimeRoot = RetroBatPaths.EmulationStationGameInfosThemeRoot,
            ArchiveRoot = RetroBatPaths.ThemeGameInfosResourcesRoot,
            LegacyHiscoreExists = Directory.Exists(Path.Combine(RetroBatPaths.EmulationStationThemesRoot, ".hiscore")),
            LegacyPanelsExists = Directory.Exists(Path.Combine(RetroBatPaths.EmulationStationThemesRoot, ".panels"))
        };

        AddAuditRoot(result, result.RuntimeRoot, runtime: true);
        AddAuditRoot(result, result.ArchiveRoot, runtime: false);
        result.SystemCount = result.Systems.Select(system => system.SystemId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        result.XmlFiles = result.Systems.Sum(system => system.XmlFiles);
        result.PanelXmlFiles = result.Systems.Sum(system => system.PanelXmlFiles);
        result.HiscoreXmlFiles = result.Systems.Sum(system => system.HiscoreXmlFiles);
        result.ControlMarkers = result.Systems.Sum(system => system.ControlMarkers);
        result.VerticalMarkers = result.Systems.Sum(system => system.VerticalMarkers);
        return result;
    }

    private static void AddAuditRoot(DatasThemeAuditResult result, string root, bool runtime)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var systemFile in Directory.EnumerateFiles(root, "*.xml", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var systemId = Path.GetFileNameWithoutExtension(systemFile);
            if (string.IsNullOrWhiteSpace(systemId))
            {
                continue;
            }

            var entry = new DatasThemeAuditSystemEntry
            {
                SystemId = systemId,
                Scope = runtime ? "runtime" : "archive",
                Path = systemFile,
                SystemXmlExists = true,
                XmlFiles = 1,
                PanelXmlFiles = FileContainsComment(systemFile, "APIEXPOSE:CPO:") ? 1 : 0,
                HiscoreXmlFiles = FileContainsComment(systemFile, "APIEXPOSE:HISCORE:") || HasHiscoreBlock(systemFile) ? 1 : 0
            };
            result.Systems.Add(entry);
        }

        foreach (var systemDirectory in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var systemId = Path.GetFileName(systemDirectory);
            if (string.IsNullOrWhiteSpace(systemId))
            {
                continue;
            }

            var entry = new DatasThemeAuditSystemEntry
            {
                SystemId = systemId,
                Scope = runtime ? "runtime" : "archive",
                Path = systemDirectory,
                SystemXmlExists = File.Exists(Path.Combine(root, systemId + ".xml"))
            };

            var xmlFiles = Directory.EnumerateFiles(systemDirectory, "*.xml", SearchOption.TopDirectoryOnly).ToList();
            entry.XmlFiles = xmlFiles.Count;
            foreach (var file in xmlFiles)
            {
                if (FileContainsComment(file, "APIEXPOSE:CPO:"))
                {
                    entry.PanelXmlFiles++;
                }

                if (FileContainsComment(file, "APIEXPOSE:HISCORE:") || HasHiscoreBlock(file))
                {
                    entry.HiscoreXmlFiles++;
                }
            }

            entry.ControlMarkers = CountFiles(Path.Combine(systemDirectory, "control"));
            entry.VerticalMarkers = CountFiles(Path.Combine(systemDirectory, "vertical"));
            result.Systems.Add(entry);
        }
    }

    private static bool FileContainsComment(string path, string marker)
    {
        try
        {
            return File.ReadAllText(path).Contains(marker, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static int CountFiles(string directory)
    {
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count()
            : 0;
    }

    private static string BuildPreviewXml(PanelThemeSnapshot layout)
    {
        var document = CreateThemeDocument();
        ApplyPanelBlock(document, layout);
        return Serialize(document);
    }

    public async Task<DatasThemeFileResult> UpdateHiscoreAsync(GameReference game, HiscoreExtractionResult hiscore, CancellationToken cancellationToken = default)
    {
        var systemId = string.IsNullOrWhiteSpace(game.SystemId) ? hiscore.System : game.SystemId;
        if (string.IsNullOrWhiteSpace(systemId))
        {
            systemId = "unknown";
        }

        var rom = !string.IsNullOrWhiteSpace(hiscore.RomName)
            ? hiscore.RomName
            : Path.GetFileNameWithoutExtension(game.GamePath);
        if (string.IsNullOrWhiteSpace(rom))
        {
            rom = "unknown";
        }

        var targetSystemId = CanonicalizeThemeSystemId(systemId, rom);
        var targetPath = ResolveThemePath(targetSystemId, rom);
        var archivePath = ResolveArchivePath(targetSystemId, rom);
        if (!_runtimeOptions.IsHighScoreExposeEnabled())
        {
            return new DatasThemeFileResult(targetSystemId, rom, targetPath, archivePath, false, "High Score Expose disabled.");
        }

        var document = LoadExistingDocument(targetPath, archivePath);
        var changed = ApplyHiscoreBlock(document, hiscore.Scores);
        if (!changed)
        {
            return new DatasThemeFileResult(targetSystemId, rom, targetPath, archivePath, false, "Hiscore block already up to date.");
        }

        await WriteMirroredXmlAsync(document, targetPath, archivePath, cancellationToken);
        return new DatasThemeFileResult(targetSystemId, rom, targetPath, archivePath, true, "Hiscore block updated.");
    }

    private async Task<DatasThemeFileResult> ExportPanelAsync(string systemId, string? rom, CancellationToken cancellationToken)
    {
        var snapshot = ResolveSnapshotWithConfiguredLayout(systemId, rom, null);
        var targetSystemId = CanonicalizeThemeSystemId(systemId, rom);
        var targetPath = ResolveThemePath(targetSystemId, rom);
        var archivePath = ResolveArchivePath(targetSystemId, rom);
        var document = LoadExistingDocument(targetPath, archivePath);
        var changed = false;

        var markersChanged = false;
        if (_runtimeOptions.IsCpoControlPanelExposeEnabled() && snapshot.Layouts.Count > 0)
        {
            changed = ApplyPanelBlock(document, snapshot) || changed;
            if (!string.IsNullOrWhiteSpace(rom) && ShouldWriteControlMarkers(snapshot))
            {
                markersChanged = WriteControlMarkers(targetSystemId, rom, snapshot) || markersChanged;
            }
        }
        else
        {
            changed = RemovePanelBlock(document) || changed;
        }

        if (!changed && !markersChanged)
        {
            return new DatasThemeFileResult(targetSystemId, rom ?? string.Empty, targetPath, archivePath, false, "Panel block already up to date.");
        }

        if (changed)
        {
            await WriteMirroredXmlAsync(document, targetPath, archivePath, cancellationToken);
        }

        return new DatasThemeFileResult(targetSystemId, rom ?? string.Empty, targetPath, archivePath, true, "Panel block exported.");
    }

    private async Task<(int Scanned, int Changed)> ImportLegacyHiscoreFilesAsync(CancellationToken cancellationToken)
    {
        var changed = 0;
        var scanned = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in EnumerateLegacyHiscoreXmlFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveLegacyHiscoreTarget(path, out var systemId, out var rom))
            {
                continue;
            }

            var targetSystemId = CanonicalizeThemeSystemId(systemId, rom);
            var key = targetSystemId + "|" + rom;
            if (!seen.Add(key))
            {
                continue;
            }

            scanned++;
            var lines = ReadLegacyHiscoreLines(path);
            if (lines.Count == 0)
            {
                continue;
            }

            var targetPath = ResolveThemePath(targetSystemId, rom);
            var archivePath = ResolveArchivePath(targetSystemId, rom);
            var document = LoadExistingDocument(targetPath, archivePath);
            if (!ApplyHiscoreTextBlock(document, lines))
            {
                continue;
            }

            await WriteMirroredXmlAsync(document, targetPath, archivePath, cancellationToken);
            changed++;
        }

        return (scanned, changed);
    }

    private async Task<(int Scanned, int Changed)> MigrateLegacyArcadeGameInfosAsync(CancellationToken cancellationToken)
    {
        var scanned = 0;
        var changed = 0;
        foreach (var root in new[] { RetroBatPaths.EmulationStationGameInfosThemeRoot, RetroBatPaths.ThemeGameInfosResourcesRoot })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var systemId in ArcadeLikeSystems.Where(systemId => !systemId.Equals("arcade", StringComparison.OrdinalIgnoreCase)))
            {
                var systemDir = Path.Combine(root, systemId);
                if (!Directory.Exists(systemDir))
                {
                    continue;
                }

                foreach (var path in Directory.EnumerateFiles(systemDir, "*.xml", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    scanned++;
                    var rom = Path.GetFileNameWithoutExtension(path);
                    var lines = ReadLegacyHiscoreLines(path);
                    if (lines.Count == 0)
                    {
                        continue;
                    }

                    var targetPath = ResolveThemePath("arcade", rom);
                    var archivePath = ResolveArchivePath("arcade", rom);
                    var document = LoadExistingDocument(targetPath, archivePath);
                    if (!ApplyHiscoreTextBlock(document, lines))
                    {
                        continue;
                    }

                    await WriteMirroredXmlAsync(document, targetPath, archivePath, cancellationToken);
                    changed++;
                }
            }
        }

        return (scanned, changed);
    }

    private static IEnumerable<string> EnumerateLegacyHiscoreXmlFiles()
    {
        var roots = new[]
        {
            Path.Combine(RetroBatPaths.EmulationStationThemesRoot, ".hiscore"),
            RetroBatPaths.ThemeHiscoreResourcesRoot
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories)
                         .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".parsingdb" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static bool TryResolveLegacyHiscoreTarget(string path, out string systemId, out string rom)
    {
        systemId = string.Empty;
        rom = Path.GetFileNameWithoutExtension(path);
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(rom))
        {
            return false;
        }

        systemId = Path.GetFileName(directory);
        return !string.IsNullOrWhiteSpace(systemId);
    }

    private static IReadOnlyList<string> ReadLegacyHiscoreLines(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            return document.Descendants()
                .Where(element =>
                    string.Equals(element.Name.LocalName, "text", StringComparison.OrdinalIgnoreCase) &&
                    HiscoreLineNameRegex.IsMatch(element.Attribute("name")?.Value ?? string.Empty))
                .Select(element => element.Elements("text").FirstOrDefault()?.Value?.Trim() ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task<(int Scanned, int Changed)> ExportArcadeThemeMarkersAsync(CancellationToken cancellationToken)
    {
        var scanned = 0;
        var changed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ResetArcadeMarkerRoots();
        foreach (var systemId in EnumerateInstalledArcadeLikeSystems())
        {
            foreach (var rom in EnumerateSystemGamelistRoms(systemId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(NormalizeRomName(rom)))
                {
                    continue;
                }

                scanned++;
                if (IsVerticalArcadeRom(rom))
                {
                    changed += WriteArcadeMarker("vertical", string.Empty, rom);
                }
            }
        }

        await Task.CompletedTask;
        return (scanned, changed);
    }

    private static IEnumerable<string> EnumerateInstalledArcadeLikeSystems()
    {
        foreach (var systemId in ArcadeLikeSystems)
        {
            var gamelistPath = Path.Combine(RetroBatPaths.RomsRoot, systemId, "gamelist.xml");
            if (File.Exists(gamelistPath))
            {
                yield return systemId;
            }
        }
    }

    private IEnumerable<(string SystemId, string Rom)> EnumerateExportableGameEntries(IReadOnlyList<PanelCatalogEntrySnapshot> systemEntries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _panels.ListPanels("game"))
        {
            if (string.IsNullOrWhiteSpace(entry.SystemId) || string.IsNullOrWhiteSpace(entry.Rom))
            {
                continue;
            }

            var targetSystemId = CanonicalizeThemeSystemId(entry.SystemId, entry.Rom);
            var key = targetSystemId + "|" + entry.Rom;
            if (seen.Add(key))
            {
                yield return (entry.SystemId, entry.Rom);
            }
        }
    }

    private static IEnumerable<string> EnumerateSystemGamelistRoms(string systemId)
    {
        var gamelistPath = Path.Combine(RetroBatPaths.RomsRoot, systemId, "gamelist.xml");
        if (!File.Exists(gamelistPath))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var stream = File.Open(gamelistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            return (document.Root?.Elements("game") ?? Enumerable.Empty<XElement>())
                .Select(game => game.Element("path")?.Value?.Trim() ?? string.Empty)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFileNameWithoutExtension(path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)))
                .Where(rom => !string.IsNullOrWhiteSpace(rom))
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private PanelThemeSnapshot ResolveSnapshotWithConfiguredLayout(string? systemId, string? rom, string? core)
    {
        var activeLayout = _runtimeOptions.GetDatasThemeExposePanelLayout(systemId ?? string.Empty);
        return _panels.GetSnapshot(systemId, rom, core, activeLayout);
    }

    private static XDocument LoadExistingDocument(string targetPath, string archivePath)
    {
        foreach (var path in new[] { targetPath, archivePath })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
                if (document.Root?.Name.LocalName.Equals("theme", StringComparison.OrdinalIgnoreCase) == true)
                {
                    EnsureView(document);
                    return document;
                }
            }
            catch
            {
                // Recreate the consolidated file if a previous export is malformed or locked mid-write.
            }
        }

        return CreateThemeDocument();
    }

    private static XDocument CreateThemeDocument()
    {
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("theme",
                new XElement("view", new XAttribute("name", ViewName))));
    }

    private static XElement EnsureView(XDocument document)
    {
        if (document.Root == null)
        {
            document.Add(new XElement("theme"));
        }

        var view = document.Root!.Elements("view")
            .FirstOrDefault(element => string.Equals(element.Attribute("name")?.Value, ViewName, StringComparison.OrdinalIgnoreCase));
        if (view != null)
        {
            return view;
        }

        view = new XElement("view", new XAttribute("name", ViewName));
        document.Root.Add(view);
        return view;
    }

    private static bool ApplyPanelBlock(XDocument document, PanelThemeSnapshot snapshot)
    {
        var view = EnsureView(document);
        var before = Serialize(view);
        RemovePanelBlock(document);

        var activeLayout = snapshot.Layouts.FirstOrDefault(layout => string.Equals(layout.Id, snapshot.ActiveLayoutId, StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Layouts.FirstOrDefault();
        if (activeLayout == null)
        {
            return before != Serialize(view);
        }

        view.Add(new XComment(" APIEXPOSE:CPO:BEGIN "));
        view.Add(CreateTextElement("panel_layout", activeLayout.Id));
        var controls = BuildControlElements(activeLayout);
        for (var i = 0; i < controls.Count; i++)
        {
            var name = controls.Count == 1 ? "control" : $"control{i + 1}";
            view.Add(CreateTextElement(name, controls[i].Type, controls[i].Color));
        }

        foreach (var player in activeLayout.Players.OrderBy(player => player.Index))
        {
            foreach (var button in player.Buttons.Where(button => button.Slot.HasValue).OrderBy(button => button.Slot!.Value))
            {
                if (IsIgnorableBlackButton(button))
                {
                    continue;
                }

                var name = player.Index <= 1
                    ? $"button{button.Slot!.Value}"
                    : $"p{player.Index}button{button.Slot!.Value}";
                view.Add(CreateTextElement(name, BestButtonText(button), button.Color));
            }
        }

        foreach (var button in activeLayout.SystemButtons)
        {
            var name = button.Id.ToLowerInvariant() switch
            {
                "start" => "buttonstart",
                "coin" => "buttoncoin",
                "select" => "buttonselect",
                _ => string.Empty
            };
            if (!string.IsNullOrWhiteSpace(name))
            {
                view.Add(CreateTextElement(name, BestButtonText(button), button.Color));
            }
        }

        view.Add(new XComment(" APIEXPOSE:CPO:END "));
        return !string.Equals(before, Serialize(view), StringComparison.Ordinal);
    }

    private static List<(string Type, string Color)> BuildControlElements(PanelThemeLayoutSnapshot layout)
    {
        var controls = new List<(string Type, string Color)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in layout.Players.OrderBy(player => player.Index))
        {
            if (string.IsNullOrWhiteSpace(player.JoystickType))
            {
                continue;
            }

            var type = player.JoystickType.Trim();
            var color = player.JoystickColor?.Trim() ?? string.Empty;
            var key = type + "\u001f" + color;
            if (seen.Add(key))
            {
                controls.Add((type, color));
            }
        }

        return controls;
    }

    private static bool RemovePanelBlock(XDocument document)
    {
        var view = EnsureView(document);
        var before = Serialize(view);
        foreach (var node in view.Nodes().ToList())
        {
            if (node is XElement element &&
                string.Equals(element.Name.LocalName, "text", StringComparison.OrdinalIgnoreCase) &&
                ButtonTextNameRegex.IsMatch(element.Attribute("name")?.Value ?? string.Empty))
            {
                node.Remove();
                continue;
            }

            if (node is XComment comment &&
                comment.Value.Contains("APIEXPOSE:CPO:", StringComparison.OrdinalIgnoreCase))
            {
                node.Remove();
            }
        }

        return !string.Equals(before, Serialize(view), StringComparison.Ordinal);
    }

    private static bool ApplyHiscoreBlock(XDocument document, IReadOnlyList<HiscoreEntry> scores)
    {
        var view = EnsureView(document);
        var before = Serialize(view);
        foreach (var node in view.Nodes().ToList())
        {
            if (node is XElement element &&
                string.Equals(element.Name.LocalName, "text", StringComparison.OrdinalIgnoreCase) &&
                HiscoreLineNameRegex.IsMatch(element.Attribute("name")?.Value ?? string.Empty))
            {
                node.Remove();
                continue;
            }

            if (node is XComment comment &&
                comment.Value.Contains("APIEXPOSE:HISCORE:", StringComparison.OrdinalIgnoreCase))
            {
                node.Remove();
            }
        }

        if (scores.Count > 0)
        {
            view.Add(new XComment(" APIEXPOSE:HISCORE:BEGIN "));
            for (var i = 0; i < scores.Count; i++)
            {
                var score = scores[i];
                view.Add(CreateTextElement($"hiscoreline{i + 1}", $"#{score.Rank} {score.Name} {score.Score}"));
            }
            view.Add(new XComment(" APIEXPOSE:HISCORE:END "));
        }

        return !string.Equals(before, Serialize(view), StringComparison.Ordinal);
    }

    private static bool ApplyHiscoreTextBlock(XDocument document, IReadOnlyList<string> lines)
    {
        var view = EnsureView(document);
        var before = Serialize(view);
        RemoveHiscoreBlock(view);

        if (lines.Count > 0)
        {
            view.Add(new XComment(" APIEXPOSE:HISCORE:BEGIN "));
            for (var i = 0; i < lines.Count; i++)
            {
                view.Add(CreateTextElement($"hiscoreline{i + 1}", lines[i]));
            }
            view.Add(new XComment(" APIEXPOSE:HISCORE:END "));
        }

        return !string.Equals(before, Serialize(view), StringComparison.Ordinal);
    }

    private static void RemoveHiscoreBlock(XElement view)
    {
        foreach (var node in view.Nodes().ToList())
        {
            if (node is XElement element &&
                string.Equals(element.Name.LocalName, "text", StringComparison.OrdinalIgnoreCase) &&
                HiscoreLineNameRegex.IsMatch(element.Attribute("name")?.Value ?? string.Empty))
            {
                node.Remove();
                continue;
            }

            if (node is XComment comment &&
                comment.Value.Contains("APIEXPOSE:HISCORE:", StringComparison.OrdinalIgnoreCase))
            {
                node.Remove();
            }
        }
    }

    private static XElement CreateTextElement(string name, string value, string? color = null)
    {
        var element = new XElement("text",
            new XAttribute("name", name),
            new XAttribute("extra", "true"),
            new XElement("text", value ?? string.Empty));

        var colorToken = NormalizeColorToken(color);
        if (!string.IsNullOrWhiteSpace(colorToken))
        {
            element.Add(new XElement("color", colorToken));
        }

        return element;
    }

    private static string BestButtonText(PanelThemeButtonSnapshot button)
    {
        return FirstNonEmpty(button.Function, button.Label, button.MachineButton, button.Controller, button.Id);
    }

    private static bool IsIgnorableBlackButton(PanelThemeButtonSnapshot button)
    {
        if (!string.Equals(button.Color?.Trim(), "black", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(button.Function))
        {
            return false;
        }

        var meaningfulText = FirstNonEmpty(button.MachineButton, button.Label);
        if (string.IsNullOrWhiteSpace(meaningfulText))
        {
            return true;
        }

        return string.Equals(meaningfulText, button.Controller, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(meaningfulText, button.Id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(meaningfulText, "NONE", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }

    private static string NormalizeColorToken(string? color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return string.Empty;
        }

        var trimmed = color.Trim();
        if (trimmed.StartsWith("${", StringComparison.Ordinal) || trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var normalized = Regex.Replace(trimmed.ToLowerInvariant(), "[^a-z0-9_]+", string.Empty);
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : "${" + normalized + "}";
    }

    private static bool WriteControlMarkers(string systemId, string rom, PanelThemeSnapshot snapshot)
    {
        var changed = false;
        var safeSystem = SanitizePathPart(systemId);
        var safeRom = SanitizePathPart(rom);
        var controlRoot = Path.Combine(RetroBatPaths.EmulationStationGameInfosThemeRoot, safeSystem, "control");
        foreach (var deviceType in KnownDeviceTypes)
        {
            var marker = Path.Combine(controlRoot, deviceType, safeRom + ".txt");
            if (File.Exists(marker))
            {
                File.Delete(marker);
                changed = true;
            }
        }

        var activeLayout = snapshot.Layouts.FirstOrDefault(layout => string.Equals(layout.Id, snapshot.ActiveLayoutId, StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Layouts.FirstOrDefault();
        var deviceTypes = activeLayout?.Players
            .Select(player => player.JoystickType)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => SanitizePathPart(value.ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? new List<string>();

        foreach (var deviceType in deviceTypes)
        {
            var marker = Path.Combine(controlRoot, deviceType, safeRom + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            if (!File.Exists(marker))
            {
                File.WriteAllText(marker, "1");
                changed = true;
            }
        }

        return changed;
    }

    private static bool ShouldWriteControlMarkers(PanelThemeSnapshot snapshot)
    {
        return string.Equals(snapshot.Scope, "game", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(snapshot.GamePanelFile);
    }

    private bool IsVerticalArcadeRom(string rom)
    {
        return GetArcadeVerticalRoms().Contains(NormalizeRomName(rom));
    }

    private static void ResetArcadeMarkerRoots()
    {
        foreach (var root in new[] { RetroBatPaths.EmulationStationGameInfosThemeRoot, RetroBatPaths.ThemeGameInfosResourcesRoot })
        {
            var arcadeRoot = Path.Combine(root, "arcade");
            foreach (var markerRoot in new[] { "vertical", "players", "category", "region", "flags" })
            {
                var path = Path.Combine(arcadeRoot, markerRoot);
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
        }
    }

    private static int WriteArcadeMarker(string markerRoot, string markerName, string rom)
    {
        var written = 0;
        foreach (var root in new[] { RetroBatPaths.EmulationStationGameInfosThemeRoot, RetroBatPaths.ThemeGameInfosResourcesRoot })
        {
            var directory = string.IsNullOrWhiteSpace(markerName)
                ? Path.Combine(root, "arcade", markerRoot)
                : Path.Combine(root, "arcade", markerRoot, markerName);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, SanitizePathPart(rom) + ".txt");
            File.WriteAllText(path, "1");
            written++;
        }

        return written;
    }

    private HashSet<string> GetArcadeVerticalRoms()
    {
        lock (_verticalRomCacheLock)
        {
            if (_arcadeVerticalRoms != null)
            {
                return _arcadeVerticalRoms;
            }

            var roms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dataRoot = Path.Combine(RetroBatPaths.PluginRoot, "resources", "gamelist", "systems_data_games");
            if (!Directory.Exists(dataRoot))
            {
                dataRoot = Path.Combine(RetroBatPaths.PluginRoot, "resources", "gamelist", "system_groups");
            }

            foreach (var fileName in new[] { "mame.json", "fbneo.json", "_ARCH_mame.json", "mame.jsonl", "fbneo.jsonl", "_ARCH_mame.jsonl" })
            {
                AddVerticalRomsFromGroupFile(roms, Path.Combine(dataRoot, fileName));
            }

            _arcadeVerticalRoms = roms;
            return _arcadeVerticalRoms;
        }
    }

    private static void AddVerticalRomsFromGroupFile(HashSet<string> roms, string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (IsCompactGamelistEntry(root))
                {
                    if (!root.TryGetProperty("ori", out var orientationProperty) ||
                        orientationProperty.ValueKind != JsonValueKind.String ||
                        !string.Equals(orientationProperty.GetString(), "vertical", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    AddJsonStringProperty(roms, root, "id");
                    AddJsonStringProperty(roms, root, "set");
                    AddJsonStringProperty(roms, root, "fn");
                    continue;
                }

                if (!root.TryGetProperty("t", out var verticalProperty) ||
                    verticalProperty.ValueKind != JsonValueKind.Number ||
                    verticalProperty.GetInt32() != 1)
                {
                    continue;
                }

                AddJsonStringProperty(roms, root, "p");
                AddJsonStringProperty(roms, root, "pref");
                AddJsonStringArray(roms, root, "r");
            }
            catch
            {
                // Ignore malformed reference lines; the export must remain best-effort.
            }
        }
    }

    private static bool IsCompactGamelistEntry(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("grp", out _) &&
            root.TryGetProperty("id", out _);
    }

    private static void AddJsonStringProperty(HashSet<string> values, JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            AddNormalizedRom(values, property.GetString());
        }
    }

    private static void AddJsonStringArray(HashSet<string> values, JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                AddNormalizedRom(values, item.GetString());
            }
        }
    }

    private static void AddNormalizedRom(HashSet<string> values, string? value)
    {
        var normalized = NormalizeRomName(value);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            values.Add(normalized);
        }
    }

    private static string NormalizeRomName(string? value)
    {
        return Path.GetFileNameWithoutExtension((value ?? string.Empty).Trim())
            .ToLowerInvariant();
    }

    private static bool WriteVerticalMarker(string systemId, string rom, bool isVertical)
    {
        var changed = false;
        foreach (var root in new[] { RetroBatPaths.EmulationStationGameInfosThemeRoot, RetroBatPaths.ThemeGameInfosResourcesRoot })
        {
            var marker = Path.Combine(root, SanitizePathPart(systemId), "vertical", SanitizePathPart(rom) + ".txt");
            if (isVertical)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                if (!File.Exists(marker))
                {
                    File.WriteAllText(marker, "1");
                    changed = true;
                }
            }
            else if (File.Exists(marker))
            {
                File.Delete(marker);
                changed = true;
            }
        }

        return changed;
    }

    private int CleanupRedundantGameInfoFiles()
    {
        var removed = 0;
        foreach (var root in new[] { RetroBatPaths.EmulationStationGameInfosThemeRoot, RetroBatPaths.ThemeGameInfosResourcesRoot })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var systemId = Path.GetFileName(directory);
                if (string.IsNullOrWhiteSpace(systemId))
                {
                    continue;
                }

                if (IsArcadeLikeSystem(systemId) && !systemId.Equals("arcade", StringComparison.OrdinalIgnoreCase))
                {
                    removed += DeleteDirectoryContents(directory);
                    TryDeleteDirectoryTree(directory);
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(directory, "*.xml", SearchOption.TopDirectoryOnly).ToList())
                {
                    var rom = Path.GetFileNameWithoutExtension(file);
                    if (HasHiscoreBlock(file) || HasGameSpecificPanel(systemId, rom))
                    {
                        continue;
                    }

                    File.Delete(file);
                    removed++;
                }

                TryDeleteDirectory(directory);
            }
        }

        return removed;
    }

    private static int DeleteDirectoryContents(string directory)
    {
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToList())
        {
            File.Delete(file);
            removed++;
        }

        return removed;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; export correctness does not depend on directory removal.
        }
    }

    private static void TryDeleteDirectoryTree(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; export correctness does not depend on directory removal.
        }
    }

    private static bool HasHiscoreBlock(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            return document.Descendants()
                .Any(element =>
                    string.Equals(element.Name.LocalName, "text", StringComparison.OrdinalIgnoreCase) &&
                    HiscoreLineNameRegex.IsMatch(element.Attribute("name")?.Value ?? string.Empty));
        }
        catch
        {
            return true;
        }
    }

    private bool HasGameSpecificPanel(string systemId, string rom)
    {
        var snapshot = _panels.GetGameSnapshot(systemId, rom);
        return ShouldWriteControlMarkers(snapshot);
    }

    private static string CanonicalizeThemeSystemId(string systemId, string? rom)
    {
        return !string.IsNullOrWhiteSpace(rom) && IsArcadeLikeSystem(systemId)
            ? "arcade"
            : systemId;
    }

    private static bool IsArcadeLikeSystem(string systemId)
    {
        return ArcadeLikeSystems.Contains((systemId ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveThemePath(string systemId, string? rom)
    {
        return string.IsNullOrWhiteSpace(rom)
            ? Path.Combine(RetroBatPaths.EmulationStationGameInfosThemeRoot, SanitizePathPart(systemId) + ".xml")
            : Path.Combine(RetroBatPaths.EmulationStationGameInfosThemeRoot, SanitizePathPart(systemId), SanitizePathPart(rom) + ".xml");
    }

    private static string ResolveArchivePath(string systemId, string? rom)
    {
        return string.IsNullOrWhiteSpace(rom)
            ? Path.Combine(RetroBatPaths.ThemeGameInfosResourcesRoot, SanitizePathPart(systemId) + ".xml")
            : Path.Combine(RetroBatPaths.ThemeGameInfosResourcesRoot, SanitizePathPart(systemId), SanitizePathPart(rom) + ".xml");
    }

    private static async Task WriteMirroredXmlAsync(XDocument document, string targetPath, string archivePath, CancellationToken cancellationToken)
    {
        var xml = SerializeDocument(document);
        await WriteIfChangedAsync(targetPath, xml, cancellationToken);
        await WriteIfChangedAsync(archivePath, xml, cancellationToken);
    }

    private static async Task WriteIfChangedAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            var current = await File.ReadAllTextAsync(path, cancellationToken);
            if (string.Equals(current, content, StringComparison.Ordinal))
            {
                return;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, new UTF8Encoding(false), cancellationToken);
        if (File.Exists(path))
        {
            File.Copy(tempPath, path, true);
            File.Delete(tempPath);
        }
        else
        {
            File.Move(tempPath, path);
        }
    }

    private static string Serialize(XNode node)
    {
        return node.ToString(SaveOptions.None);
    }

    private static string SerializeDocument(XDocument document)
    {
        RemoveWhitespaceTextNodes(document);
        using var stream = new MemoryStream();
        var settings = new System.Xml.XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            IndentChars = "\t",
            NewLineChars = Environment.NewLine,
            NewLineHandling = System.Xml.NewLineHandling.Replace,
            OmitXmlDeclaration = false
        };

        using (var writer = System.Xml.XmlWriter.Create(stream, settings))
        {
            document.Save(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void RemoveWhitespaceTextNodes(XDocument document)
    {
        foreach (var text in document.DescendantNodes().OfType<XText>().ToList())
        {
            if (string.IsNullOrWhiteSpace(text.Value))
            {
                text.Remove();
            }
        }
    }

    private static string SanitizePathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder((value ?? string.Empty).Trim());
        for (var i = 0; i < builder.Length; i++)
        {
            if (invalid.Contains(builder[i]))
            {
                builder[i] = '_';
            }
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }
}

public sealed record DatasThemeFileResult(
    string SystemId,
    string Rom,
    string ThemePath,
    string ArchivePath,
    bool Written,
    string Message);

public sealed class DatasThemeExportResult
{
    public bool Enabled { get; set; }
    public bool CpoEnabled { get; set; }
    public bool HighScoreEnabled { get; set; }
    public int SystemFilesScanned { get; set; }
    public int GameFilesScanned { get; set; }
    public int LegacyHiscoreFilesScanned { get; set; }
    public int LegacyHiscoreFilesChanged { get; set; }
    public int LegacyGameInfosScanned { get; set; }
    public int LegacyGameInfosChanged { get; set; }
    public int VerticalMarkersScanned { get; set; }
    public int VerticalMarkersChanged { get; set; }
    public int RedundantFilesRemoved { get; set; }
    public int FilesChanged { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class DatasThemeAuditResult
{
    public string RuntimeRoot { get; set; } = string.Empty;
    public string ArchiveRoot { get; set; } = string.Empty;
    public bool LegacyHiscoreExists { get; set; }
    public bool LegacyPanelsExists { get; set; }
    public int SystemCount { get; set; }
    public int XmlFiles { get; set; }
    public int PanelXmlFiles { get; set; }
    public int HiscoreXmlFiles { get; set; }
    public int ControlMarkers { get; set; }
    public int VerticalMarkers { get; set; }
    public List<DatasThemeAuditSystemEntry> Systems { get; set; } = new();
}

public sealed class DatasThemeAuditSystemEntry
{
    public string SystemId { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool SystemXmlExists { get; set; }
    public int XmlFiles { get; set; }
    public int PanelXmlFiles { get; set; }
    public int HiscoreXmlFiles { get; set; }
    public int ControlMarkers { get; set; }
    public int VerticalMarkers { get; set; }
}

public sealed class DatasThemeHiscoreWriter : IHiscoreThemeWriter
{
    private readonly DatasThemeExposeService _datasThemeExposeService;

    public DatasThemeHiscoreWriter(DatasThemeExposeService datasThemeExposeService)
    {
        _datasThemeExposeService = datasThemeExposeService;
    }

    public async Task WriteAsync(GameReference game, HiscoreExtractionResult result, CancellationToken cancellationToken = default)
    {
        await _datasThemeExposeService.UpdateHiscoreAsync(game, result, cancellationToken);
    }
}

public sealed class CompositeHiscoreThemeWriter : IHiscoreThemeWriter
{
    private readonly DatasThemeHiscoreWriter _datasThemeWriter;
    private readonly EmulationStationHiscoreThemeWriter _legacyHiscoreWriter;
    private readonly ApiExposeRuntimeOptionsService _runtimeOptions;

    public CompositeHiscoreThemeWriter(
        DatasThemeHiscoreWriter datasThemeWriter,
        EmulationStationHiscoreThemeWriter legacyHiscoreWriter,
        ApiExposeRuntimeOptionsService runtimeOptions)
    {
        _datasThemeWriter = datasThemeWriter;
        _legacyHiscoreWriter = legacyHiscoreWriter;
        _runtimeOptions = runtimeOptions;
    }

    public async Task WriteAsync(GameReference game, HiscoreExtractionResult result, CancellationToken cancellationToken = default)
    {
        await _datasThemeWriter.WriteAsync(game, result, cancellationToken);
        if (_runtimeOptions.IsLegacyHiscoreThemeExportEnabled())
        {
            await _legacyHiscoreWriter.WriteAsync(game, result, cancellationToken);
        }
    }
}
