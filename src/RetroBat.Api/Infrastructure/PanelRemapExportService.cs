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
        var slots = ReadTemplateLayoutSlots(dynpanel.Value, selectedLayout, buttonsPerPlayerSetting, out var layoutUsed);
        if (slots.Count == 0)
        {
            slots = ReadDynpanelSlots(dynpanel.Value);
            layoutUsed = "convention";
        }

        if (slots.Count == 0)
        {
            Trace($"skipped {systemId}: dynpanel has no slot mapping");
            return;
        }

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
    private static IReadOnlyList<DynpanelSlot> ReadTemplateLayoutSlots(JsonElement root, string? selectedLayout, int buttonsPerPlayer, out string layoutUsed)
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

            // Legacy-validated formula (LPEvents.py, confirmed on Score Master):
            // the rmp line label comes from the CONTROLLER name of the layout button,
            // the value from its retropad_id. The rmp_button field encodes the slot's
            // physical libretro identity and must NOT be used as the label.
            var controller = entry.Value.TryGetProperty("controller", out var ctl) ? ctl.GetString() : null;
            var label = ControllerToRmpLabel(controller);
            var retropad = entry.Value.TryGetProperty("retropad_id", out var rp) && rp.TryGetInt32(out var id) ? id : -1;
            var slot = entry.Value.TryGetProperty("panel_slot", out var ps) && ps.TryGetInt32(out var s)
                ? s
                : int.TryParse(entry.Name, out var n) ? n : -1;

            if (label is not null && retropad >= 0 && slot > 0)
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

    private static string? ControllerToRmpLabel(string? controller)
    {
        return controller?.Trim().ToUpperInvariant() switch
        {
            "A" => "a",
            "B" => "b",
            "X" => "x",
            "Y" => "y",
            "PAGEUP" => "l",
            "PAGEDOWN" => "r",
            "L2" => "l2",
            "R2" => "r2",
            "L3" => "l3",
            "R3" => "r3",
            "START" => "start",
            "SELECT" or "COIN" => "select",
            _ => null
        };
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

    private void WriteGuarded(string targetPath, string body, string systemId, string game, string reason)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))[..16];
        var content = $"{MarkerPrefix} hash={hash}\n{body}";

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

            if (!existingFirstLine.StartsWith(MarkerPrefix, StringComparison.Ordinal))
            {
                Trace($"KEPT user file untouched (no marker): {targetPath}");
                return;
            }

            if (existingFirstLine.Contains($"hash={hash}", StringComparison.Ordinal))
            {
                Trace($"up to date, no rewrite: {targetPath}");
                return;
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
        Trace($"WROTE ({reason}): {targetPath} [{body.Split('\n').Length} lines]");
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
