using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using RetroBat.Domain.Events;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Generates RetroArch per-game input remaps (.rmp) from the dynpanel data and the
/// user's declared control panel, so LED colors and actual game inputs stay in sync
/// on every machine without shipping precooked, wiring-specific files.
///
/// Modern successor of the legacy LedPanel-Manager LPEvents.py logic (see
/// projects-source/logic-gen-remap.md): triggered on game selection and regenerated
/// when the Control Panel Manager settings change in EmulationStation.
///
/// Safety rules: never overwrites a file it did not generate (marker + hash),
/// backs up before rewriting, disabled unless ApiExpose:PanelRemapExport:Enabled.
/// MAME core gets no .rmp by doctrine (the shared MAME cfg would double-remap);
/// MAME standalone cfg merge is a later phase.
/// </summary>
public sealed class PanelRemapExportService : IHostedService, IDisposable
{
    private const string MarkerPrefix = "# generated-by-apiexpose-panel-remap";

    private readonly IEventBus _eventBus;
    private readonly ApiContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PanelRemapExportService>? _logger;
    private readonly object _generateLock = new();
    private IDisposable? _subscription;

    public PanelRemapExportService(
        IEventBus eventBus,
        ApiContext context,
        IConfiguration configuration,
        ILogger<PanelRemapExportService>? logger = null)
    {
        _eventBus = eventBus;
        _context = context;
        _configuration = configuration;
        _logger = logger;
    }

    private bool Enabled => _configuration.GetValue("ApiExpose:PanelRemapExport:Enabled", false);

    /// <summary>
    /// "manual" (default): browsing the gamelist costs nothing — remaps are pushed
    /// through the deploy endpoints (LedManagerSetup "Contrôles"), and a CONTROL
    /// PANEL selector change still regenerates immediately. "onSelection": legacy
    /// behavior, regenerate at every game selection.
    /// </summary>
    private string Mode => _configuration.GetValue("ApiExpose:PanelRemapExport:Mode", "manual") ?? "manual";

    private static string TracePath => Path.Combine(RetroBatPaths.RuntimeLogRoot, "panel-remap.log");

    /// <summary>File trace next to the other .log files: the API console is hidden,
    /// so this is the only way for the user (and support) to see what was decided.</summary>
    private void Trace(string message)
    {
        try
        {
            Directory.CreateDirectory(RetroBatPaths.RuntimeLogRoot);
            File.AppendAllText(TracePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}\n");
        }
        catch
        {
            // never let logging break the pipeline
        }

        _logger?.LogInformation("[panel-remap] {Message}", message);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _eventBus.Subscribe<EventEnvelope>(OnEvent);
        _logger?.LogInformation("Panel remap export {State}.", Enabled ? "enabled" : "disabled (ApiExpose:PanelRemapExport:Enabled=false)");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _subscription?.Dispose();

    private void OnEvent(EventEnvelope envelope)
    {
        if (!Enabled)
        {
            return;
        }

        var isGameSelection = string.Equals(envelope.Type, "ui.game.selected.raw", StringComparison.OrdinalIgnoreCase);
        var isPanelSettingsChange = string.Equals(envelope.Type, "panel.settings.changed", StringComparison.OrdinalIgnoreCase);
        if (!isGameSelection && !isPanelSettingsChange)
        {
            return;
        }

        if (isGameSelection && !string.Equals(Mode, "onSelection", StringComparison.OrdinalIgnoreCase))
        {
            return; // manual mode: scrolling the gamelist must not trigger any work
        }

        try
        {
            var selected = isGameSelection
                ? ReadSelection(envelope) ?? _context.Ui.Selected
                : _context.Ui.Selected;

            if (selected is null || string.IsNullOrWhiteSpace(selected.SystemId) || string.IsNullOrWhiteSpace(selected.GamePath))
            {
                Trace($"event={envelope.Type} but no game context (system={selected?.SystemId ?? "-"} game={selected?.GamePath ?? "-"}) -> nothing to do");
                return;
            }

            Trace($"event={envelope.Type} system={selected.SystemId} game={selected.GamePath}");
            lock (_generateLock)
            {
                GenerateForGame(selected.SystemId!, selected.GamePath!, reason: isPanelSettingsChange ? "panel-settings-changed" : "game-selected");
            }
        }
        catch (Exception ex)
        {
            Trace($"FAILED: {ex.Message}");
            _logger?.LogWarning(ex, "Panel remap export failed.");
        }
    }

    private static GameReference? ReadSelection(EventEnvelope envelope)
    {
        if (envelope.Payload is null)
        {
            return null;
        }

        var selection = envelope.Payload.GetType().GetProperty("Selection")?.GetValue(envelope.Payload);
        if (selection is null)
        {
            return null;
        }

        string? Read(string name) => selection.GetType().GetProperty(name)?.GetValue(selection) as string;
        return new GameReference
        {
            SystemId = Read("SystemId"),
            GamePath = Read("GamePath"),
            GameName = Read("GameName")
        };
    }

    private void GenerateForGame(string systemId, string gamePath, string reason)
    {
        var (emulator, core) = ResolveEmulatorAndCore(systemId);
        Trace($"resolve {systemId}: emulator={emulator ?? "?"} core={core ?? "?"}");
        if (!string.Equals(emulator, "libretro", StringComparison.OrdinalIgnoreCase))
        {
            Trace($"skipped {systemId}: emulator '{emulator ?? "?"}' is not libretro (only libretro in this phase)");
            return;
        }

        if (string.IsNullOrWhiteSpace(core))
        {
            Trace($"skipped {systemId}: no core resolved");
            return;
        }

        if (core.Contains("mame", StringComparison.OrdinalIgnoreCase))
        {
            // Doctrine: arcade via the shared MAME cfg only; a .rmp on the MAME core
            // would stack with it unpredictably.
            Trace($"skipped {systemId}: MAME core is cfg-driven by doctrine");
            return;
        }

        var dynpanel = LoadDynpanel(systemId, RomNameFrom(gamePath));
        if (dynpanel is null)
        {
            Trace($"skipped {systemId}: no dynpanel data");
            return;
        }

        var buttonsPerPlayerSetting = ReadIntSetting("global.apiexpose.control_manager.buttons_per_player", 6);

        // The ES "CONTROL PANEL" selector (per system, per game) picks a named
        // system_template layout: e.g. snes.apiexpose_panel_snes = "6-Button:Score Master".
        var selectedLayout = ResolvePanelLayoutSelection(systemId, Path.GetFileName(gamePath));
        var slots = new List<DynpanelSlot>(ReadTemplateLayoutSlots(dynpanel.Value, selectedLayout, buttonsPerPlayerSetting, CabinetButtons, out var layoutUsed));
        if (slots.Count == 0)
        {
            slots.AddRange(ReadDynpanelSlots(dynpanel.Value));
            layoutUsed = "convention";
        }

        if (slots.Count == 0)
        {
            Trace($"skipped {systemId}: dynpanel has no slot mapping");
            return;
        }

        ParkUnusedIdentities(slots);

        var coreFolder = ResolveRemapFolder(core);
        if (coreFolder is null)
        {
            Trace($"skipped {systemId}: no corename found in emulators/retroarch/info/{core}_libretro.info");
            return;
        }

        var playerCount = Math.Clamp(ReadIntSetting("global.apiexpose.control_manager.player_count", 2), 1, 8);
        var targetDir = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "config", "remaps", coreFolder);
        var body = BuildRmp(slots, playerCount);

        // RetroArch precedence: game > content-directory > core. The system panel is
        // expressed as a CONTENT-DIRECTORY remap (named after the roms folder), which
        // covers every game of the system and beats a generic <core>.rmp, without
        // bleeding into other systems sharing the same core (e.g. Genesis Plus GX).
        var contentDirName = Path.GetFileName(Path.GetDirectoryName(gamePath)?.TrimEnd('\\', '/') ?? systemId);
        if (string.IsNullOrWhiteSpace(contentDirName))
        {
            contentDirName = systemId;
        }

        WriteGuarded(Path.Combine(targetDir, contentDirName + ".rmp"), body, systemId, contentDirName, $"{reason} scope=content-dir layout={layoutUsed}");

        // A per-GAME remap is only written when the user made a game-specific choice
        // (per-game CONTROL PANEL selector) or a per-game dynpanel exists: otherwise
        // the content-dir remap is enough and we do not litter the remaps folder.
        var gameFileName = Path.GetFileNameWithoutExtension(gamePath);
        var hasGameSelector = ReadStringSetting($"{systemId}[\"{Path.GetFileName(gamePath)}\"].apiexpose_panel_{systemId.ToLowerInvariant()}") is not null;
        var hasGameDynpanel = File.Exists(Path.Combine(RetroBatPaths.PluginRoot, "resources", "dynpanels", "games", RomNameFrom(gamePath) + ".json"));
        if (hasGameSelector || hasGameDynpanel)
        {
            WriteGuarded(Path.Combine(targetDir, gameFileName + ".rmp"), body, systemId, gameFileName, $"{reason} scope=game layout={layoutUsed}");
        }
    }

    /// <summary>
    /// Cabinet buttons the layout does not use would still act as their native
    /// RetroPad identity (e.g. our buttons 7/8 arrive as l/r and would ghost L/R on
    /// a 6-button layout): park them on R3, dead for every template layout.
    /// A dark LED must be a dead button.
    /// </summary>
    private void ParkUnusedIdentities(List<DynpanelSlot> slots)
    {
        var usedIdentities = slots.Select(s => s.LibretroButton).ToHashSet(StringComparer.Ordinal);
        var parkedCount = 0;
        foreach (var identity in CabinetButtons.Values.Distinct())
        {
            if (!usedIdentities.Contains(identity))
            {
                slots.Add(new DynpanelSlot(900 + parkedCount++, identity, 15));
            }
        }
    }

    public sealed record RemapDeployItem(string SystemId, string Status, string Detail);

    public sealed record RemapDeployReport(int Total, int Written, int UpToDate, int Skipped, IReadOnlyList<RemapDeployItem> Items);

    /// <summary>
    /// Push deployment for the LedManagerSetup "Contrôles" view: regenerates the
    /// content-directory remap of one system, or of every system that has a dynpanel.
    /// Same pipeline and write guards as the event path.
    /// </summary>
    public RemapDeployReport DeployRemaps(string? systemId = null)
    {
        var systems = string.IsNullOrWhiteSpace(systemId)
            ? ListDynpanelSystems()
            : new[] { systemId.Trim().ToLowerInvariant() };

        var items = new List<RemapDeployItem>();
        lock (_generateLock)
        {
            foreach (var system in systems)
            {
                RemapDeployItem item;
                try
                {
                    item = DeploySystemRemap(system);
                }
                catch (Exception ex)
                {
                    item = new RemapDeployItem(system, "failed", ex.Message);
                }

                Trace($"deploy {system}: {item.Status} ({item.Detail})");
                items.Add(item);
            }
        }

        var written = items.Count(i => i.Status == "written");
        var upToDate = items.Count(i => i.Status == "up-to-date");
        return new RemapDeployReport(items.Count, written, upToDate, items.Count - written - upToDate, items);
    }

    private static IReadOnlyList<string> ListDynpanelSystems()
    {
        var dir = Path.Combine(RetroBatPaths.DynPanelsRoot, "systems");
        if (!Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!.ToLowerInvariant())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>System-level twin of GenerateForGame: no per-game selector, no
    /// per-game rmp — just the content-directory remap of the system.</summary>
    private RemapDeployItem DeploySystemRemap(string systemId)
    {
        var (emulator, core) = ResolveEmulatorAndCore(systemId);
        if (!string.Equals(emulator, "libretro", StringComparison.OrdinalIgnoreCase))
        {
            return new RemapDeployItem(systemId, "skipped", $"emulator '{emulator ?? "?"}' is not libretro");
        }

        if (string.IsNullOrWhiteSpace(core))
        {
            return new RemapDeployItem(systemId, "skipped", "no core resolved");
        }

        if (core.Contains("mame", StringComparison.OrdinalIgnoreCase))
        {
            return new RemapDeployItem(systemId, "skipped", "MAME core is cfg-driven by doctrine");
        }

        var dynpanel = LoadDynpanel(systemId, "");
        if (dynpanel is null)
        {
            return new RemapDeployItem(systemId, "skipped", "no dynpanel data");
        }

        var buttonsPerPlayer = ReadIntSetting("global.apiexpose.control_manager.buttons_per_player", 6);
        var selectedLayout = ResolvePanelLayoutSelection(systemId, "");
        var slots = new List<DynpanelSlot>(ReadTemplateLayoutSlots(dynpanel.Value, selectedLayout, buttonsPerPlayer, CabinetButtons, out var layoutUsed));
        if (slots.Count == 0)
        {
            slots.AddRange(ReadDynpanelSlots(dynpanel.Value));
            layoutUsed = "convention";
        }

        if (slots.Count == 0)
        {
            return new RemapDeployItem(systemId, "skipped", "dynpanel has no slot mapping");
        }

        ParkUnusedIdentities(slots);

        var coreFolder = ResolveRemapFolder(core);
        if (coreFolder is null)
        {
            return new RemapDeployItem(systemId, "skipped", $"no corename in {core}_libretro.info");
        }

        var playerCount = Math.Clamp(ReadIntSetting("global.apiexpose.control_manager.player_count", 2), 1, 8);
        var targetDir = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "config", "remaps", coreFolder);
        var body = BuildRmp(slots, playerCount);
        var contentDirName = ReadSystemRomsFolder(systemId) ?? systemId;
        var status = WriteGuarded(Path.Combine(targetDir, contentDirName + ".rmp"), body, systemId, contentDirName, $"deploy scope=content-dir layout={layoutUsed}");
        return new RemapDeployItem(systemId, status, $"layout={layoutUsed} file={coreFolder}\\{contentDirName}.rmp");
    }

    /// <summary>Roms folder name of a system (es_systems.cfg &lt;path&gt;) — the name
    /// RetroArch expects for a content-directory remap.</summary>
    private string? ReadSystemRomsFolder(string systemId)
    {
        try
        {
            var path = Path.Combine(RetroBatPaths.RetroBatRoot, "emulationstation", ".emulationstation", "es_systems.cfg");
            if (!File.Exists(path))
            {
                return null;
            }

            var doc = XDocument.Load(path);
            var system = doc.Root?.Elements("system")
                .FirstOrDefault(s => string.Equals((string?)s.Element("name"), systemId, StringComparison.OrdinalIgnoreCase));
            var romsPath = (string?)system?.Element("path");
            if (string.IsNullOrWhiteSpace(romsPath))
            {
                return null;
            }

            var name = Path.GetFileName(romsPath.TrimEnd('\\', '/', ' '));
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private (string? Emulator, string? Core) ResolveEmulatorAndCore(string systemId)
    {
        // es_settings overrides win; es_systems provides the defaults.
        var settingsEmulator = ReadStringSetting($"{systemId}.emulator");
        var settingsCore = ReadStringSetting($"{systemId}.core");
        if (!string.IsNullOrWhiteSpace(settingsEmulator) && !string.IsNullOrWhiteSpace(settingsCore))
        {
            return (settingsEmulator, settingsCore);
        }

        var (defaultEmulator, defaultCore) = ReadSystemDefaults(systemId);
        return (settingsEmulator ?? defaultEmulator, settingsCore ?? defaultCore);
    }

    private (string? Emulator, string? Core) ReadSystemDefaults(string systemId)
    {
        try
        {
            var path = Path.Combine(RetroBatPaths.RetroBatRoot, "emulationstation", ".emulationstation", "es_systems.cfg");
            if (!File.Exists(path))
            {
                return (null, null);
            }

            var doc = XDocument.Load(path);
            var system = doc.Root?.Elements("system")
                .FirstOrDefault(s => string.Equals((string?)s.Element("name"), systemId, StringComparison.OrdinalIgnoreCase));
            var emulator = system?.Element("emulators")?.Elements("emulator").FirstOrDefault();
            var core = emulator?.Element("cores")?.Elements("core")
                .FirstOrDefault(c => string.Equals((string?)c.Attribute("default"), "true", StringComparison.OrdinalIgnoreCase))
                ?? emulator?.Element("cores")?.Elements("core").FirstOrDefault();
            return ((string?)emulator?.Attribute("name"), core?.Value?.Trim());
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Unable to read es_systems defaults for {System}.", systemId);
            return (null, null);
        }
    }

    private string? ResolveRemapFolder(string core)
    {
        try
        {
            var infoPath = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "info", core + "_libretro.info");
            if (File.Exists(infoPath))
            {
                foreach (var line in File.ReadLines(infoPath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("corename", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = trimmed.Split('=', 2)[1].Trim().Trim('"');
                        // Legacy folder corrections carried over from LedPanel-Manager.
                        return value switch
                        {
                            "Caprice32" => "cap32",
                            "Dolphin" => "dolphin-emu",
                            _ => value
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Unable to read corename for {Core}.", core);
        }

        return null;
    }

    private static string RomNameFrom(string gamePath)
    {
        return Path.GetFileNameWithoutExtension(gamePath).Trim().ToLowerInvariant().Replace(' ', '_');
    }

    private JsonElement? LoadDynpanel(string systemId, string rom)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(RetroBatPaths.PluginRoot, "resources", "dynpanels", "games", rom + ".json"),
                     Path.Combine(RetroBatPaths.PluginRoot, "resources", "dynpanels", "systems", systemId.ToLowerInvariant() + ".json")
                 })
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(candidate));
                return doc.RootElement.Clone();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Unable to parse dynpanel {Path}.", candidate);
            }
        }

        return null;
    }

    private sealed record DynpanelSlot(int Slot, string LibretroButton, int RetropadId);

    private static readonly IReadOnlyDictionary<int, string> DefaultCabinetButtons = new Dictionary<int, string>
    {
        [1] = "b",
        [2] = "a",
        [3] = "y",
        [4] = "x",
        [5] = "l2",
        [6] = "r2",
        [7] = "l",
        [8] = "r"
    };

    private IReadOnlyDictionary<int, string>? _cabinetButtonsCache;

    /// <summary>
    /// Physical cartography of the cabinet: the RetroPad identity each panel button
    /// reaches RetroArch as, measured end to end (wiring + encoder + SDL resolution +
    /// launcher). The LED pipeline is anchored on the same `physical` numbers, so
    /// lights and inputs stay in sync by construction. Overridable in appsettings.json
    /// (ApiExpose:PanelRemapExport:CabinetButtons): if a RetroBat update changes the
    /// controller chain again, adjust the map — no rebuild.
    /// </summary>
    private IReadOnlyDictionary<int, string> CabinetButtons
    {
        get
        {
            if (_cabinetButtonsCache is not null)
            {
                return _cabinetButtonsCache;
            }

            var configured = new Dictionary<int, string>();
            foreach (var child in _configuration.GetSection("ApiExpose:PanelRemapExport:CabinetButtons").GetChildren())
            {
                if (int.TryParse(child.Key, out var number) && number > 0 && !string.IsNullOrWhiteSpace(child.Value))
                {
                    configured[number] = child.Value.Trim().ToLowerInvariant();
                }
            }

            return _cabinetButtonsCache = configured.Count > 0 ? configured : DefaultCabinetButtons;
        }
    }

    /// <summary>Reads the CONTROL PANEL selector: game override first, then system, empty = AUTO.</summary>
    private string? ResolvePanelLayoutSelection(string systemId, string gameFileName)
    {
        var key = $"apiexpose_panel_{systemId.ToLowerInvariant()}";
        var value = ReadStringSetting($"{systemId}[\"{gameFileName}\"].{key}")
                    ?? ReadStringSetting($"{systemId}.{key}");
        return string.IsNullOrWhiteSpace(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
    }

    /// <summary>
    /// Reads the buttons of a system_template layout. `selectedLayout` comes from the
    /// ES CONTROL PANEL selector; when null (AUTO) the layout matching the declared
    /// cabinet button count is chosen (exact "N-Button", else first "N-Button:*",
    /// else the largest layout that fits).
    /// </summary>
    private static IReadOnlyList<DynpanelSlot> ReadTemplateLayoutSlots(JsonElement root, string? selectedLayout, int buttonsPerPlayer, IReadOnlyDictionary<int, string> cabinetButtons, out string layoutUsed)
    {
        layoutUsed = selectedLayout ?? "auto";
        if (!root.TryGetProperty("system_template", out var template) && !root.TryGetProperty("core_template", out template))
        {
            return Array.Empty<DynpanelSlot>();
        }

        if (!template.TryGetProperty("layouts", out var layouts) || layouts.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<DynpanelSlot>();
        }

        JsonElement layout = default;
        var found = false;

        if (selectedLayout is not null)
        {
            found = layouts.TryGetProperty(selectedLayout, out layout);
        }

        if (!found)
        {
            var names = layouts.EnumerateObject().Select(p => p.Name).ToArray();
            var pick = names.FirstOrDefault(n => n.Equals($"{buttonsPerPlayer}-Button", StringComparison.OrdinalIgnoreCase))
                       ?? names.FirstOrDefault(n => n.StartsWith($"{buttonsPerPlayer}-Button:", StringComparison.OrdinalIgnoreCase))
                       ?? names.Where(n => LeadingCount(n) <= buttonsPerPlayer)
                               .OrderByDescending(LeadingCount)
                               .FirstOrDefault();
            if (pick is null)
            {
                return Array.Empty<DynpanelSlot>();
            }

            layoutUsed = pick + " (auto)";
            found = layouts.TryGetProperty(pick, out layout);
        }

        if (!found || !layout.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<DynpanelSlot>();
        }

        var slots = new List<DynpanelSlot>();
        foreach (var entry in buttons.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // The rmp line remaps the CABINET button that carries the function: the
            // layout's `physical` field — the LED pipeline lights that same button, so
            // anchoring on it keeps lights and inputs in sync by construction.
            // Label = the cabinet cartography's identity for that physical button;
            // value = retropad_id, the id the core expects for the button's game
            // function. Neither `controller` (ES name on the ORIGINAL accessory) nor
            // `rmp_button` (keyed on panel_slot, the accessory position) describe the
            // cabinet — both desynced LEDs from inputs (seen on snes Score Master).
            var retropad = entry.Value.TryGetProperty("retropad_id", out var rp) && rp.TryGetInt32(out var id) ? id : -1;
            var slot = entry.Value.TryGetProperty("panel_slot", out var ps) && ps.TryGetInt32(out var s)
                ? s
                : int.TryParse(entry.Name, out var n) ? n : -1;
            var physical = entry.Value.TryGetProperty("physical", out var ph) && ph.TryGetInt32(out var p) && p > 0
                ? p
                : slot;

            // a template button without a function carries no action: parked on R3
            // (LED off = dead button) whatever residual id the data may hold
            var function = entry.Value.TryGetProperty("function", out var fn) ? fn.GetString() : null;
            if (string.IsNullOrWhiteSpace(function) || function!.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                retropad = 15;
            }

            if (cabinetButtons.TryGetValue(physical, out var label) && retropad >= 0 && slot > 0)
            {
                slots.Add(new DynpanelSlot(slot, label, retropad));
            }
        }

        return slots.OrderBy(s => s.Slot).ToArray();

        static int LeadingCount(string name)
        {
            var dash = name.IndexOf('-');
            return dash > 0 && int.TryParse(name[..dash], out var count) ? count : 0;
        }
    }

    private static IReadOnlyList<DynpanelSlot> ReadDynpanelSlots(JsonElement root)
    {
        var slots = new List<DynpanelSlot>();
        if (!root.TryGetProperty("panel", out var panel) || !panel.TryGetProperty("slots", out var slotsElement)
            || slotsElement.ValueKind != JsonValueKind.Object)
        {
            return slots;
        }

        foreach (var entry in slotsElement.EnumerateObject())
        {
            if (!int.TryParse(entry.Name, out var slot))
            {
                continue;
            }

            var libretro = entry.Value.TryGetProperty("libretro_button", out var lb) ? lb.GetString() : null;
            var retropad = entry.Value.TryGetProperty("retropad_id", out var rp) && rp.TryGetInt32(out var id) ? id : -1;
            if (!string.IsNullOrWhiteSpace(libretro) && retropad >= 0)
            {
                slots.Add(new DynpanelSlot(slot, libretro!, retropad));
            }
        }

        return slots.OrderBy(s => s.Slot).ToArray();
    }

    /// <summary>
    /// Generates the PER-GAME FBNeo RetroArch remap from the game dynpanel and the
    /// cabinet cartography: game button n placed on physical slot P becomes
    /// btn_{carto[P]} = panel.slots[n].retropad_id (the id the core expects for
    /// that game button under the retrobat_standard convention); unused identities
    /// are parked on R3. Same guard rules as the system remaps (marker + hash);
    /// a RetroArch boilerplate file without any btn line carries no user mapping
    /// and is replaced.
    /// </summary>
    public string DeployFbneoGameRemap(string rom)
    {
        var gamePath = Path.Combine(RetroBatPaths.PluginRoot, "resources", "dynpanels", "games", rom + ".json");
        if (!File.Exists(gamePath))
        {
            return "missing";
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(gamePath));
            var root = doc.RootElement;
            if (!root.TryGetProperty("panel", out var panel) || !panel.TryGetProperty("slots", out var panelSlots)
                || !root.TryGetProperty("players", out var players) || !players.TryGetProperty("1", out var player1)
                || !player1.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Object)
            {
                return "missing";
            }

            var layoutId = $"{Math.Clamp(CabinetButtons.Count, 2, 8)}-Button";
            JsonElement layoutButtons = default;
            var hasLayoutButtons = player1.TryGetProperty("layouts", out var layouts)
                && layouts.TryGetProperty(layoutId, out var layout)
                && layout.TryGetProperty("buttons", out layoutButtons)
                && layoutButtons.ValueKind == JsonValueKind.Object;

            var slots = new List<DynpanelSlot>();
            foreach (var button in buttons.EnumerateObject())
            {
                int? physical = null;
                if (button.Value.TryGetProperty("layouts", out var buttonLayouts)
                    && buttonLayouts.TryGetProperty(layoutId, out var buttonLayout)
                    && buttonLayout.TryGetProperty("panel_slot", out var direct) && direct.ValueKind == JsonValueKind.Number)
                {
                    physical = direct.GetInt32();
                }

                if (physical is null && hasLayoutButtons && layoutButtons.TryGetProperty(button.Name, out var placed)
                    && placed.TryGetProperty("panel_slot", out var indirect) && indirect.ValueKind == JsonValueKind.Number)
                {
                    physical = indirect.GetInt32();
                }

                if (physical is not { } slotNumber || !CabinetButtons.TryGetValue(slotNumber, out var identity))
                {
                    continue;
                }

                // purely data-driven: the id the core expects for game button n
                // comes from the dynpanel convention (panel.slots[n].retropad_id) —
                // family quirks belong to the DATA, never hardcoded here;
                // "2#2" duplicates share their base button's id
                var baseButton = button.Name.Split('#')[0];
                if (!panelSlots.TryGetProperty(baseButton, out var template)
                    || !template.TryGetProperty("retropad_id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                slots.Add(new DynpanelSlot(slotNumber, identity, idElement.GetInt32()));
            }

            if (slots.Count == 0)
            {
                return "missing";
            }

            ParkUnusedIdentities(slots);
            var body = BuildRmp(slots, playerCount: 2);
            var targetDir = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "config", "remaps", "FinalBurn Neo");
            Directory.CreateDirectory(targetDir);
            var targetPath = Path.Combine(targetDir, rom + ".rmp");
            if (File.Exists(targetPath) && !File.ReadLines(targetPath)
                    .Any(l => l.StartsWith("input_player", StringComparison.OrdinalIgnoreCase) && l.Contains("_btn_")))
            {
                File.Delete(targetPath);
            }

            // a target identical to the curated pack is one of the legacy
            // deployments in disguise: upgradable to the generated remap
            var legacyPack = Path.Combine(RetroBatPaths.PluginRoot, "resources", "controls", "retroarch", "fbneo", rom + ".rmp");
            if (File.Exists(targetPath) && File.Exists(legacyPack))
            {
                static string Norm(string text) => string.Join("\n",
                    text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
                if (Norm(File.ReadAllText(targetPath)) == Norm(File.ReadAllText(legacyPack)))
                {
                    File.Delete(targetPath);
                }
            }

            return WriteGuarded(targetPath, body, "fbneo", rom, "per-game remap");
        }
        catch
        {
            return "failed";
        }
    }

    private static string BuildRmp(IReadOnlyList<DynpanelSlot> slots, int playerCount)
    {
        var sb = new StringBuilder();
        for (var player = 1; player <= playerCount; player++)
        {
            sb.AppendLine($"input_libretro_device_p{player} = \"1\"");
            sb.AppendLine($"input_player{player}_analog_dpad_mode = \"0\"");
            foreach (var slot in slots)
            {
                sb.AppendLine($"input_player{player}_btn_{slot.LibretroButton} = \"{slot.RetropadId}\"");
            }
        }

        return sb.ToString();
    }

    private string WriteGuarded(string targetPath, string body, string systemId, string game, string reason)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..16];
        var content = $"{MarkerPrefix} hash={hash}\n{body}";
        var registry = LoadRegistry();
        var registryKey = targetPath.ToLowerInvariant();

        if (File.Exists(targetPath))
        {
            string existingFirstLine;
            try
            {
                existingFirstLine = File.ReadLines(targetPath).FirstOrDefault() ?? "";
            }
            catch
            {
                existingFirstLine = "";
            }

            var hasMarker = existingFirstLine.StartsWith(MarkerPrefix, StringComparison.Ordinal);
            var ownedPerRegistry = registry.TryGetValue(registryKey, out var lastWrittenHash);

            // RetroArch rewrites remap files when saving from its menus and strips our
            // comment marker. The registry remembers what we wrote, so a reformatted
            // file is still recognized as ours; a file we never wrote stays sacred.
            if (!hasMarker && !ownedPerRegistry)
            {
                Trace($"KEPT user file untouched (never written by us): {targetPath}");
                return "kept-user-file";
            }

            if (hash == lastWrittenHash || (hasMarker && existingFirstLine.Contains($"hash={hash}", StringComparison.Ordinal)))
            {
                Trace($"up to date (target content unchanged): {targetPath}");
                return "up-to-date";
            }

            try
            {
                File.Copy(targetPath, targetPath + ".bak", overwrite: true);
            }
            catch
            {
                // best effort backup
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        File.WriteAllText(targetPath, content);
        registry[registryKey] = hash;
        SaveRegistry(registry);
        Trace($"WROTE ({reason}): {targetPath} [{body.Split('\n').Length} lines]");
        return "written";
    }

    private static string RegistryPath => Path.Combine(RetroBatPaths.PluginRoot, "state", "panel-remap-registry.json");

    private Dictionary<string, string> LoadRegistry()
    {
        try
        {
            if (File.Exists(RegistryPath))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(RegistryPath))
                       ?? new Dictionary<string, string>();
            }
        }
        catch (Exception ex)
        {
            Trace($"registry unreadable, starting fresh: {ex.Message}");
        }

        return new Dictionary<string, string>();
    }

    private void SaveRegistry(Dictionary<string, string> registry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RegistryPath)!);
            File.WriteAllText(RegistryPath, JsonSerializer.Serialize(registry, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Trace($"registry save failed: {ex.Message}");
        }
    }

    private string? ReadStringSetting(string name)
    {
        try
        {
            var path = Path.Combine(RetroBatPaths.RetroBatRoot, "emulationstation", ".emulationstation", "es_settings.cfg");
            if (!File.Exists(path))
            {
                return null;
            }

            var doc = XDocument.Load(path);
            return doc.Root?.Elements()
                .FirstOrDefault(e => string.Equals((string?)e.Attribute("name"), name, StringComparison.OrdinalIgnoreCase))
                ?.Attribute("value")?.Value;
        }
        catch
        {
            return null;
        }
    }

    private int ReadIntSetting(string name, int fallback)
    {
        return int.TryParse(ReadStringSetting(name), out var value) ? value : fallback;
    }
}
