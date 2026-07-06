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
                return;
            }

            lock (_generateLock)
            {
                GenerateForGame(selected.SystemId!, selected.GamePath!, reason: isPanelSettingsChange ? "panel-settings-changed" : "game-selected");
            }
        }
        catch (Exception ex)
        {
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
        if (!string.Equals(emulator, "libretro", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogDebug("Remap export skipped for {System}: emulator {Emulator} (only libretro in this phase).", systemId, emulator ?? "?");
            return;
        }

        if (string.IsNullOrWhiteSpace(core))
        {
            _logger?.LogDebug("Remap export skipped for {System}: no core resolved.", systemId);
            return;
        }

        if (core.Contains("mame", StringComparison.OrdinalIgnoreCase))
        {
            // Doctrine: arcade via the shared MAME cfg only; a .rmp on the MAME core
            // would stack with it unpredictably.
            _logger?.LogDebug("Remap export skipped for {System}: MAME core is cfg-driven by doctrine.", systemId);
            return;
        }

        var dynpanel = LoadDynpanel(systemId, RomNameFrom(gamePath));
        if (dynpanel is null)
        {
            _logger?.LogDebug("Remap export skipped for {System}: no dynpanel data.", systemId);
            return;
        }

        var slots = ReadDynpanelSlots(dynpanel.Value);
        if (slots.Count == 0)
        {
            _logger?.LogDebug("Remap export skipped for {System}: dynpanel has no slot mapping.", systemId);
            return;
        }

        var coreFolder = ResolveRemapFolder(core);
        if (coreFolder is null)
        {
            _logger?.LogDebug("Remap export skipped for {System}: no corename for core {Core}.", systemId, core);
            return;
        }

        var buttonsPerPlayer = ReadIntSetting("global.apiexpose.control_manager.buttons_per_player", 6);
        var playerCount = Math.Clamp(ReadIntSetting("global.apiexpose.control_manager.player_count", 2), 1, 8);

        var gameFileName = Path.GetFileNameWithoutExtension(gamePath);
        var targetDir = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "config", "remaps", coreFolder);
        var targetPath = Path.Combine(targetDir, gameFileName + ".rmp");

        var body = BuildRmp(slots, buttonsPerPlayer, playerCount);
        WriteGuarded(targetPath, body, systemId, gameFileName, reason);
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

    private static string BuildRmp(IReadOnlyList<DynpanelSlot> slots, int buttonsPerPlayer, int playerCount)
    {
        var sb = new StringBuilder();
        for (var player = 1; player <= playerCount; player++)
        {
            sb.AppendLine($"input_libretro_device_p{player} = \"1\"");
            sb.AppendLine($"input_player{player}_analog_dpad_mode = \"0\"");
            foreach (var slot in slots.Where(s => s.Slot <= Math.Max(1, buttonsPerPlayer)))
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
                _logger?.LogInformation("Remap export kept user file untouched: {Path}", targetPath);
                return;
            }

            if (existingFirstLine.Contains($"hash={hash}", StringComparison.Ordinal))
            {
                return; // already up to date
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
        _logger?.LogInformation("Remap exported ({Reason}): system={System} game={Game} -> {Path}", reason, systemId, game, targetPath);
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
