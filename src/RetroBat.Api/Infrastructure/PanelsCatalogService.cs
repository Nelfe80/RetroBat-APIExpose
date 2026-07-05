using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using RetroBat.Domain.Models;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public class PanelsCatalogService
{
    private readonly ApiContext _context;

    public PanelsCatalogService(ApiContext context)
    {
        _context = context;
    }

    public PanelThemeSnapshot GetCurrentSnapshot()
    {
        var game = _context.Ui.Running ?? _context.Ui.Selected;
        return GetSnapshot(
            ResolveSystemId(game),
            ResolveRomId(game),
            game?.Launch?.Core?.Trim() ?? string.Empty);
    }

    public IReadOnlyList<PanelCatalogEntrySnapshot> ListPanels(string? scope = null, string? systemId = null, string? rom = null, string? core = null)
    {
        var requestedScope = (scope ?? string.Empty).Trim();
        var requestedSystemId = NormalizeSlug(systemId ?? string.Empty);
        var requestedRom = NormalizeSlug(rom ?? string.Empty);
        var requestedCore = NormalizeSlug(core ?? string.Empty);

        var entries = new List<PanelCatalogEntrySnapshot>();
        if (string.IsNullOrWhiteSpace(requestedScope) || requestedScope.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            entries.AddRange(EnumeratePanels("system", RetroBatPaths.DynPanelsSystemsRoot, requestedSystemId, requestedRom, requestedCore));
        }

        if (string.IsNullOrWhiteSpace(requestedScope) || requestedScope.Equals("core", StringComparison.OrdinalIgnoreCase))
        {
            entries.AddRange(EnumeratePanels("core", RetroBatPaths.DynPanelsCoresRoot, requestedSystemId, requestedRom, requestedCore));
        }

        if (string.IsNullOrWhiteSpace(requestedScope) || requestedScope.Equals("game", StringComparison.OrdinalIgnoreCase))
        {
            entries.AddRange(EnumeratePanels("game", RetroBatPaths.DynPanelsGamesRoot, requestedSystemId, requestedRom, requestedCore));
        }

        return entries
            .OrderBy(entry => entry.Scope, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.SystemId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Core, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Rom, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public PanelThemeSnapshot GetSystemSnapshot(string systemId, string? activeLayoutId = null)
    {
        return GetSnapshot(systemId, null, null, activeLayoutId);
    }

    public PanelThemeSnapshot GetGameSnapshot(string systemId, string rom, string? core = null, string? activeLayoutId = null)
    {
        return GetSnapshot(systemId, rom, core, activeLayoutId);
    }

    public PanelThemeSnapshot GetSnapshot(string? systemId, string? rom, string? core = null, string? activeLayoutId = null)
    {
        var resolvedSystemId = (systemId ?? string.Empty).Trim();
        var resolvedRom = NormalizeSlug(rom ?? string.Empty);
        var resolvedCore = (core ?? string.Empty).Trim();

        var gamePanelFile = FindExistingFile(RetroBatPaths.DynPanelsGamesRoot, GetRomCandidates(resolvedRom));
        if (!IsGamePanelCompatibleWithSystem(gamePanelFile, resolvedSystemId))
        {
            gamePanelFile = string.Empty;
        }

        var snapshot = new PanelThemeSnapshot
        {
            SystemId = resolvedSystemId,
            Rom = resolvedRom,
            Core = resolvedCore,
            SystemPanelFile = FindExistingFile(RetroBatPaths.DynPanelsSystemsRoot, new[] { resolvedSystemId }),
            CorePanelFile = FindExistingFile(RetroBatPaths.DynPanelsCoresRoot, GetCoreCandidates(resolvedCore)),
            GamePanelFile = gamePanelFile,
            ThemePanelPath = string.IsNullOrWhiteSpace(resolvedSystemId)
                ? string.Empty
                : Path.Combine(".panels", resolvedSystemId, string.IsNullOrWhiteSpace(resolvedRom) ? "_system.xml" : $"{resolvedRom}.xml")
        };

        if (!string.IsNullOrWhiteSpace(snapshot.GamePanelFile))
        {
            snapshot.Scope = "game";
            var gameRoot = LoadJson(snapshot.GamePanelFile);
            if (gameRoot != null)
            {
                snapshot.Layouts = BuildGameLayouts(gameRoot);
            }
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.SystemPanelFile))
        {
            snapshot.Scope = "system";
            var systemRoot = LoadJson(snapshot.SystemPanelFile);
            if (systemRoot != null)
            {
                snapshot.Layouts = BuildSystemLayouts(systemRoot);
            }
        }

        snapshot.DefaultLayoutId = ChooseDefaultLayoutId(snapshot.Layouts);
        snapshot.ActiveLayoutId = ResolveActiveLayoutId(snapshot.Layouts, activeLayoutId, snapshot.DefaultLayoutId);
        snapshot.ActiveLayoutSource = string.IsNullOrWhiteSpace(activeLayoutId) ? "fallback" : "request";
        return snapshot;
    }

    public string GetCurrentThemeXml()
    {
        var snapshot = GetCurrentSnapshot();
        return BuildThemeXml(snapshot);
    }

    public string GetThemeXml(string? systemId, string? rom, string? core = null, string? activeLayoutId = null)
    {
        var snapshot = GetSnapshot(systemId, rom, core, activeLayoutId);
        return BuildThemeXml(snapshot);
    }

    public async Task<string> ExportThemeXmlAsync(string? systemId, string? rom, string? core = null, string? activeLayoutId = null, CancellationToken cancellationToken = default)
    {
        var snapshot = GetSnapshot(systemId, rom, core, activeLayoutId);
        var xml = BuildThemeXml(snapshot);
        if (snapshot.Layouts.Count == 0 || string.IsNullOrWhiteSpace(snapshot.SystemId))
        {
            return string.Empty;
        }

        var fileName = string.IsNullOrWhiteSpace(snapshot.Rom) ? "_system.xml" : $"{snapshot.Rom}.xml";
        var themeDir = Path.Combine(RetroBatPaths.EmulationStationPanelsThemeRoot, snapshot.SystemId);
        var archiveDir = Path.Combine(RetroBatPaths.ThemePanelsResourcesRoot, snapshot.SystemId);
        var themePath = Path.Combine(themeDir, fileName);
        var archivePath = Path.Combine(archiveDir, fileName);

        Directory.CreateDirectory(themeDir);
        Directory.CreateDirectory(archiveDir);

        await WriteFileAtomicallyAsync(themePath, xml, cancellationToken);
        await WriteFileAtomicallyAsync(archivePath, xml, cancellationToken);
        return themePath;
    }

    private static IEnumerable<PanelCatalogEntrySnapshot> EnumeratePanels(string scope, string root, string requestedSystemId, string requestedRom, string requestedCore)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var entry = BuildCatalogEntry(scope, file);
            if (entry == null)
            {
                continue;
            }

            if (!MatchesCatalogFilters(entry, requestedSystemId, requestedRom, requestedCore))
            {
                continue;
            }

            yield return entry;
        }
    }

    private static bool MatchesCatalogFilters(PanelCatalogEntrySnapshot entry, string requestedSystemId, string requestedRom, string requestedCore)
    {
        if (!string.IsNullOrWhiteSpace(requestedSystemId) &&
            !string.Equals(entry.SystemId, requestedSystemId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(requestedRom) &&
            !string.Equals(entry.Rom, requestedRom, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(requestedCore) &&
            !string.Equals(NormalizeSlug(entry.Core), requestedCore, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static PanelCatalogEntrySnapshot? BuildCatalogEntry(string scope, string filePath)
    {
        var root = LoadJson(filePath);
        if (root == null)
        {
            return null;
        }

        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var entry = new PanelCatalogEntrySnapshot
        {
            Scope = scope,
            FilePath = filePath
        };

        switch (scope)
        {
            case "system":
                entry.SystemId = fileName;
                entry.ThemePanelPath = Path.Combine(".panels", entry.SystemId, "_system.xml");
                break;
            case "core":
                entry.Core = fileName;
                break;
            case "game":
                entry.Rom = fileName;
                entry.ThemePanelPath = Path.Combine(".panels", string.Empty, entry.Rom + ".xml").TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                break;
        }

        var layouts = scope.Equals("system", StringComparison.OrdinalIgnoreCase)
            ? BuildSystemLayouts(root)
            : BuildGameLayouts(root);
        entry.LayoutIds = layouts.Select(layout => layout.Id).ToList();
        entry.LayoutCount = entry.LayoutIds.Count;
        entry.DefaultLayoutId = ChooseDefaultLayoutId(layouts);

        if (scope.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            var panel = root["panel"] as JsonObject;
            entry.SystemId = NormalizeSlug(root["system"]?.GetValue<string>() ?? entry.SystemId);
            entry.ThemePanelPath = Path.Combine(".panels", entry.SystemId, "_system.xml");
            if (panel?["profile_key"] != null)
            {
                entry.Core = panel["profile_key"]?.GetValue<string>() ?? string.Empty;
            }
        }
        else if (scope.Equals("core", StringComparison.OrdinalIgnoreCase))
        {
            entry.Core = NormalizeSlug(root["core"]?.GetValue<string>() ?? entry.Core);
        }
        else if (scope.Equals("game", StringComparison.OrdinalIgnoreCase))
        {
            var systemId = root["system"]?.GetValue<string>() ?? string.Empty;
            entry.SystemId = NormalizeSlug(systemId);
            entry.Rom = NormalizeSlug(root["rom"]?.GetValue<string>() ?? entry.Rom);
            entry.ThemePanelPath = string.IsNullOrWhiteSpace(entry.SystemId)
                ? string.Empty
                : Path.Combine(".panels", entry.SystemId, $"{entry.Rom}.xml");
        }

        return entry;
    }

    private static bool IsGamePanelCompatibleWithSystem(string gamePanelFile, string requestedSystemId)
    {
        if (string.IsNullOrWhiteSpace(gamePanelFile))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(requestedSystemId))
        {
            return true;
        }

        var root = LoadJson(gamePanelFile);
        var panelSystemId = NormalizeSlug(root?["system"]?.GetValue<string>() ?? string.Empty);
        var normalizedRequestedSystemId = NormalizeSlug(requestedSystemId);
        if (string.IsNullOrWhiteSpace(panelSystemId))
        {
            return true;
        }

        return string.Equals(panelSystemId, normalizedRequestedSystemId, StringComparison.OrdinalIgnoreCase) ||
            IsArcadeSystem(panelSystemId) && IsArcadeSystem(normalizedRequestedSystemId);
    }

    private static bool IsArcadeSystem(string systemId)
    {
        return systemId.Equals("mame", StringComparison.OrdinalIgnoreCase) ||
            systemId.Equals("fbneo", StringComparison.OrdinalIgnoreCase) ||
            systemId.Equals("arcade", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildThemeXml(PanelThemeSnapshot snapshot)
    {
        var root = new XElement("panel",
            new XAttribute("schema", "api_expose.panel.theme.v1"),
            new XAttribute("scope", snapshot.Scope),
            new XAttribute("system", snapshot.SystemId),
            new XAttribute("rom", snapshot.Rom),
            new XAttribute("defaultLayout", snapshot.DefaultLayoutId),
            new XAttribute("activeLayout", snapshot.ActiveLayoutId),
            new XAttribute("activeLayoutSource", snapshot.ActiveLayoutSource));

        if (!string.IsNullOrWhiteSpace(snapshot.Core))
        {
            root.Add(new XAttribute("core", snapshot.Core));
        }

        var sources = new XElement("sources");
        if (!string.IsNullOrWhiteSpace(snapshot.SystemPanelFile))
        {
            sources.Add(new XElement("system", snapshot.SystemPanelFile));
        }
        if (!string.IsNullOrWhiteSpace(snapshot.CorePanelFile))
        {
            sources.Add(new XElement("core", snapshot.CorePanelFile));
        }
        if (!string.IsNullOrWhiteSpace(snapshot.GamePanelFile))
        {
            sources.Add(new XElement("game", snapshot.GamePanelFile));
        }
        root.Add(sources);

        var layoutsEl = new XElement("layouts");
        foreach (var layout in snapshot.Layouts)
        {
            var layoutEl = new XElement("layout",
                new XAttribute("id", layout.Id),
                new XAttribute("type", layout.Type),
                new XAttribute("panelButtons", layout.PanelButtons));

            if (!string.IsNullOrWhiteSpace(layout.Name))
            {
                layoutEl.Add(new XAttribute("name", layout.Name));
            }

            if (!string.IsNullOrWhiteSpace(layout.ProfileKey))
            {
                layoutEl.Add(new XAttribute("profileKey", layout.ProfileKey));
            }

            var playersEl = new XElement("players");
            foreach (var player in layout.Players)
            {
                var playerEl = new XElement("player", new XAttribute("index", player.Index));
                if (!string.IsNullOrWhiteSpace(player.JoystickType) || !string.IsNullOrWhiteSpace(player.JoystickColor))
                {
                    var joystickEl = new XElement("joystick");
                    if (!string.IsNullOrWhiteSpace(player.JoystickType))
                    {
                        joystickEl.Add(new XAttribute("type", player.JoystickType));
                    }
                    if (!string.IsNullOrWhiteSpace(player.JoystickColor))
                    {
                        joystickEl.Add(new XAttribute("color", player.JoystickColor));
                    }
                    playerEl.Add(joystickEl);
                }

                var buttonsEl = new XElement("buttons");
                foreach (var button in player.Buttons)
                {
                    buttonsEl.Add(CreateButtonElement(button));
                }
                playerEl.Add(buttonsEl);
                playersEl.Add(playerEl);
            }
            layoutEl.Add(playersEl);

            var systemButtonsEl = new XElement("systemButtons");
            foreach (var button in layout.SystemButtons)
            {
                systemButtonsEl.Add(CreateButtonElement(button));
            }
            layoutEl.Add(systemButtonsEl);
            layoutsEl.Add(layoutEl);
        }
        root.Add(layoutsEl);

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
        var builder = new StringBuilder();
        using var writer = new Utf8StringWriter(builder);
        doc.Save(writer);
        return builder.ToString();
    }

    private static XElement CreateButtonElement(PanelThemeButtonSnapshot button)
    {
        var element = new XElement("button", new XAttribute("id", button.Id));
        if (button.Slot.HasValue)
        {
            element.Add(new XAttribute("slot", button.Slot.Value));
        }
        if (button.Player > 0)
        {
            element.Add(new XAttribute("player", button.Player));
        }
        AddAttr(element, "label", button.Label);
        AddAttr(element, "machineButton", button.MachineButton);
        AddAttr(element, "function", button.Function);
        AddAttr(element, "controller", button.Controller);
        AddAttr(element, "color", button.Color);
        if (button.RetropadId.HasValue)
        {
            element.Add(new XAttribute("retropadId", button.RetropadId.Value));
        }
        AddAttr(element, "libretroButton", button.LibretroButton);
        AddAttr(element, "fbneoButton", button.FbneoButton);
        AddAttr(element, "mameButton", button.MameButton);
        return element;
    }

    private static void AddAttr(XElement element, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            element.Add(new XAttribute(name, value));
        }
    }

    private static List<PanelThemeLayoutSnapshot> BuildSystemLayouts(JsonObject root)
    {
        var panel = root["panel"] as JsonObject;
        var slots = panel?["slots"] as JsonObject;
        var systemTemplate = root["system_template"] as JsonObject;
        var layouts = systemTemplate?["layouts"] as JsonObject;
        var result = new List<PanelThemeLayoutSnapshot>();
        if (layouts == null)
        {
            return result;
        }

        foreach (var layoutEntry in layouts)
        {
            if (layoutEntry.Value is not JsonObject layoutObj)
            {
                continue;
            }

            var layout = new PanelThemeLayoutSnapshot
            {
                Id = layoutEntry.Key,
                Type = layoutObj["type"]?.GetValue<string>() ?? string.Empty,
                Name = layoutObj["name"]?.GetValue<string>() ?? string.Empty,
                PanelButtons = layoutObj["panel_buttons"]?.GetValue<int?>() ?? 0,
                Scope = "system"
            };

            var player = new PanelThemePlayerSnapshot
            {
                Index = 1,
                JoystickType = "joy8way",
                JoystickColor = layoutObj["joystick"]?["color"]?.GetValue<string>() ?? string.Empty
            };

            var buttons = layoutObj["buttons"] as JsonObject;
            if (buttons != null)
            {
                foreach (var buttonEntry in buttons)
                {
                    if (buttonEntry.Value is not JsonObject buttonObj)
                    {
                        continue;
                    }

                    var button = BuildSystemButton(
                        buttonEntry.Key,
                        buttonObj,
                        slots,
                        panel?["system_slots"] as JsonObject);
                    if (IsSystemButton(buttonEntry.Key))
                    {
                        layout.SystemButtons.Add(button);
                    }
                    else
                    {
                        player.Buttons.Add(button);
                    }
                }
            }

            player.Buttons = player.Buttons
                .OrderBy(button => button.Slot ?? int.MaxValue)
                .ThenBy(button => button.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            layout.SystemButtons = layout.SystemButtons
                .OrderBy(button => button.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            layout.Players.Add(player);
            result.Add(layout);
        }

        return result;
    }

    private static PanelThemeButtonSnapshot BuildSystemButton(string id, JsonObject buttonObj, JsonObject? slots, JsonObject? systemSlots)
    {
        var slot = buttonObj["physical"]?.GetValue<int?>();
        var slotSource = IsSystemButton(id)
            ? systemSlots?[NormalizeSystemButtonKey(id)] as JsonObject
            : (slot.HasValue && slots != null ? slots[slot.Value.ToString()] as JsonObject : null);
        var function = buttonObj["function"]?.GetValue<string>() ?? string.Empty;
        var controller = buttonObj["controller"]?.GetValue<string>() ?? string.Empty;
        var gameButton = buttonObj["gameButton"]?.GetValue<string>()
            ?? buttonObj["game_button"]?.GetValue<string>()
            ?? string.Empty;

        return new PanelThemeButtonSnapshot
        {
            Id = id,
            Slot = slot,
            Label = BestButtonLabel(function, controller, id),
            MachineButton = BestMachineButton(gameButton, function, controller, id),
            Function = NormalizeNone(function),
            Controller = controller,
            Color = buttonObj["color"]?.GetValue<string>() ?? string.Empty,
            RetropadId = buttonObj["retropad_id"]?.GetValue<int?>(),
            LibretroButton = slotSource?["libretro_button"]?.GetValue<string>() ?? string.Empty,
            FbneoButton = slotSource?["fbneo_button"]?.GetValue<string>() ?? string.Empty,
            MameButton = slotSource?["mame_button"]?.GetValue<string>() ?? string.Empty
        };
    }

    private static List<PanelThemeLayoutSnapshot> BuildGameLayouts(JsonObject root)
    {
        var result = new List<PanelThemeLayoutSnapshot>();
        var players = root["players"] as JsonObject;
        var panel = root["panel"] as JsonObject;
        var slots = panel?["slots"] as JsonObject;
        var systemSlots = panel?["system_slots"] as JsonObject;
        if (players == null)
        {
            return result;
        }

        var layoutNames = players
            .Select(entry => entry.Value?["layouts"] as JsonObject)
            .Where(layouts => layouts != null)
            .SelectMany(layouts => layouts!.Select(layout => layout.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var layoutName in layoutNames)
        {
            var layout = new PanelThemeLayoutSnapshot
            {
                Id = layoutName,
                Type = layoutName.Contains(':') ? layoutName.Split(':', 2)[0] : layoutName,
                Name = layoutName.Contains(':') ? layoutName.Split(':', 2)[1] : string.Empty,
                PanelButtons = ParseLeadingInt(layoutName),
                Scope = "game"
            };

            foreach (var playerEntry in players)
            {
                if (playerEntry.Value is not JsonObject playerObj)
                {
                    continue;
                }

                var playerIndex = int.TryParse(playerEntry.Key, out var parsedPlayer) ? parsedPlayer : 0;
                var layoutObj = playerObj["layouts"]?[layoutName] as JsonObject;
                if (layoutObj == null)
                {
                    continue;
                }

                var player = new PanelThemePlayerSnapshot
                {
                    Index = playerIndex,
                    JoystickType = playerObj["devices"]?[0]?["type"]?.GetValue<string>() ?? string.Empty,
                    JoystickColor = playerObj["devices"]?[0]?["color"]?.GetValue<string>() ?? string.Empty
                };

                var playerButtons = playerObj["buttons"] as JsonObject;
                var layoutButtons = layoutObj["buttons"] as JsonObject;
                if (playerButtons != null && layoutButtons != null)
                {
                    foreach (var buttonEntry in layoutButtons)
                    {
                        var sourceButton = playerButtons[buttonEntry.Key] as JsonObject;
                        var layoutButton = buttonEntry.Value as JsonObject;
                        if (sourceButton == null || layoutButton == null)
                        {
                            continue;
                        }

                        var slot = layoutButton["panel_slot"]?.GetValue<int?>();
                        var slotSource = slot.HasValue && slots != null ? slots[slot.Value.ToString()] as JsonObject : null;
                        var logicalName = sourceButton["logical_name"]?.GetValue<string>()
                            ?? sourceButton["game_button"]?.ToString()
                            ?? buttonEntry.Key;

                        player.Buttons.Add(new PanelThemeButtonSnapshot
                        {
                            Id = buttonEntry.Key,
                            Slot = slot,
                            Player = playerIndex,
                            Label = logicalName,
                            MachineButton = logicalName,
                            Function = NormalizeNone(sourceButton["function"]?.GetValue<string>() ?? string.Empty),
                            Controller = slotSource?["retrobat_button"]?.GetValue<string>() ?? string.Empty,
                            Color = sourceButton["color"]?.GetValue<string>() ?? string.Empty,
                            RetropadId = slotSource?["retropad_id"]?.GetValue<int?>(),
                            LibretroButton = slotSource?["libretro_button"]?.GetValue<string>() ?? string.Empty,
                            FbneoButton = slotSource?["fbneo_button"]?.GetValue<string>() ?? string.Empty,
                            MameButton = sourceButton["mame"]?["input_id"]?.GetValue<string>()
                                ?? slotSource?["mame_button"]?.GetValue<string>()
                                ?? string.Empty
                        });
                    }
                }

                player.Buttons = player.Buttons
                    .OrderBy(button => button.Slot ?? int.MaxValue)
                    .ThenBy(button => button.Id, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                layout.Players.Add(player);
            }

            if (players.FirstOrDefault().Value is JsonObject firstPlayer)
            {
                var systemInputs = firstPlayer["system_inputs"] as JsonObject;
                if (systemInputs != null)
                {
                    foreach (var systemInput in systemInputs)
                    {
                        var inputObj = systemInput.Value as JsonObject;
                        if (inputObj == null)
                        {
                            continue;
                        }

                        var systemSlot = systemSlots?[systemInput.Key] as JsonObject;
                        layout.SystemButtons.Add(new PanelThemeButtonSnapshot
                        {
                            Id = systemInput.Key,
                            Label = inputObj["label"]?.GetValue<string>() ?? systemInput.Key,
                            MachineButton = inputObj["label"]?.GetValue<string>() ?? systemInput.Key,
                            Function = inputObj["label"]?.GetValue<string>() ?? systemInput.Key,
                            Controller = systemSlot?["retrobat_button"]?.GetValue<string>() ?? string.Empty,
                            Color = inputObj["color"]?.GetValue<string>() ?? string.Empty,
                            RetropadId = systemSlot?["retropad_id"]?.GetValue<int?>(),
                            LibretroButton = systemSlot?["libretro_button"]?.GetValue<string>() ?? string.Empty,
                            FbneoButton = systemSlot?["fbneo_button"]?.GetValue<string>() ?? string.Empty,
                            MameButton = inputObj["mame"]?["input_id"]?.GetValue<string>() ?? string.Empty
                        });
                    }
                }
            }

            layout.SystemButtons = layout.SystemButtons
                .DistinctBy(button => button.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(button => button.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.Add(layout);
        }

        return result;
    }

    private static string BestButtonLabel(string function, string controller, string fallback)
    {
        var normalizedFunction = NormalizeNone(function);
        if (!string.IsNullOrWhiteSpace(normalizedFunction))
        {
            return normalizedFunction;
        }

        return !string.IsNullOrWhiteSpace(controller) ? controller : fallback;
    }

    private static string BestMachineButton(string gameButton, string function, string controller, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(gameButton) &&
            !gameButton.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            return gameButton;
        }

        var normalizedFunction = NormalizeNone(function);
        if (!string.IsNullOrWhiteSpace(normalizedFunction))
        {
            return normalizedFunction;
        }

        return !string.IsNullOrWhiteSpace(controller) ? controller : fallback;
    }

    private static string NormalizeNone(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("NONE", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return value;
    }

    private static bool IsSystemButton(string id)
    {
        return id.Equals("START", StringComparison.OrdinalIgnoreCase)
            || id.Equals("COIN", StringComparison.OrdinalIgnoreCase)
            || id.Equals("SELECT", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSystemButtonKey(string id)
    {
        return id.Equals("COIN", StringComparison.OrdinalIgnoreCase) ? "coin"
            : id.Equals("SELECT", StringComparison.OrdinalIgnoreCase) ? "select"
            : "start";
    }

    private static int ParseLeadingInt(string layoutName)
    {
        var digits = new string(layoutName.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : 0;
    }

    private static string ChooseDefaultLayoutId(IEnumerable<PanelThemeLayoutSnapshot> layouts)
    {
        var layoutList = layouts.ToList();
        var fourButton = layoutList
            .Where(layout => layout.PanelButtons == 4 || string.Equals(layout.Type, "4-Button", StringComparison.OrdinalIgnoreCase))
            .OrderBy(layout => string.IsNullOrWhiteSpace(layout.Name) ? 0 : 1)
            .ThenBy(layout => layout.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (fourButton != null)
        {
            return fourButton.Id;
        }

        var unnamed = layoutList
            .Where(layout => string.IsNullOrWhiteSpace(layout.Name))
            .OrderByDescending(layout => layout.PanelButtons)
            .ThenBy(layout => layout.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (unnamed != null)
        {
            return unnamed.Id;
        }

        return layoutList
            .OrderByDescending(layout => layout.PanelButtons)
            .ThenBy(layout => layout.Id, StringComparer.OrdinalIgnoreCase)
            .Select(layout => layout.Id)
            .FirstOrDefault()
            ?? string.Empty;
    }

    private static string ResolveActiveLayoutId(IEnumerable<PanelThemeLayoutSnapshot> layouts, string? requestedLayoutId, string defaultLayoutId)
    {
        var layoutList = layouts.ToList();
        var requested = (requestedLayoutId ?? string.Empty).Trim();
        if (int.TryParse(requested, out var requestedButtons) && requestedButtons > 0)
        {
            requested = $"{requestedButtons}-Button";
        }

        if (!string.IsNullOrWhiteSpace(requested) &&
            layoutList.Any(layout => string.Equals(layout.Id, requested, StringComparison.OrdinalIgnoreCase)))
        {
            return requested;
        }

        if (!string.IsNullOrWhiteSpace(requested))
        {
            var matchingType = layoutList
                .Where(layout =>
                    string.Equals(layout.Type, requested, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals($"{layout.PanelButtons}-Button", requested, StringComparison.OrdinalIgnoreCase))
                .OrderBy(layout => string.IsNullOrWhiteSpace(layout.Name) ? 0 : 1)
                .ThenBy(layout => layout.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (matchingType != null)
            {
                return matchingType.Id;
            }

            var requestedButtonCount = ParseLeadingInt(requested);
            if (requestedButtonCount > 0)
            {
                var bestWithinCabinet = layoutList
                    .Where(layout => layout.PanelButtons > 0 && layout.PanelButtons <= requestedButtonCount)
                    .OrderByDescending(layout => layout.PanelButtons)
                    .ThenBy(layout => string.IsNullOrWhiteSpace(layout.Name) ? 0 : 1)
                    .ThenBy(layout => layout.Id, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (bestWithinCabinet != null)
                {
                    return bestWithinCabinet.Id;
                }
            }
        }

        return defaultLayoutId;
    }

    private static JsonObject? LoadJson(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
    }

    private static string FindExistingFile(string root, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate)))
        {
            var path = Path.Combine(root, candidate + ".json");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return string.Empty;
    }

    private static IEnumerable<string> GetCoreCandidates(string core)
    {
        if (string.IsNullOrWhiteSpace(core))
        {
            return Array.Empty<string>();
        }

        return new[]
        {
            core,
            core.Replace("_libretro", "", StringComparison.OrdinalIgnoreCase),
            NormalizeSlug(core.Replace("_libretro", "", StringComparison.OrdinalIgnoreCase))
        }.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetRomCandidates(string resolvedRom)
    {
        return new[]
        {
            resolvedRom,
        }.Where(candidate => !string.IsNullOrWhiteSpace(candidate))
         .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string ResolveRomId(GameReference? game)
    {
        var rawRom = Path.GetFileNameWithoutExtension(game?.GamePath ?? game?.GameName ?? string.Empty);
        var gameId = game?.GameId ?? string.Empty;
        var preferred = !string.IsNullOrWhiteSpace(rawRom) ? rawRom : gameId;
        return NormalizeSlug(preferred);
    }

    private static string ResolveSystemId(GameReference? game)
    {
        if (game == null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(game.SystemId) &&
            !string.Equals(game.SystemId, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return game.SystemId;
        }

        if (!string.IsNullOrWhiteSpace(game.Launch?.System))
        {
            return game.Launch.System.Trim();
        }

        var romPath = game.GamePath ?? string.Empty;
        try
        {
            var romsRoot = Path.GetFullPath(RetroBatPaths.RomsRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var fullGamePath = Path.GetFullPath(romPath);
            if (fullGamePath.StartsWith(romsRoot, StringComparison.OrdinalIgnoreCase))
            {
                var relative = fullGamePath[romsRoot.Length..];
                var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Length > 1)
                {
                    return parts[0];
                }
            }
        }
        catch
        {
            // Ignore path inference errors.
        }

        return game.SystemId;
    }

    private static string NormalizeSlug(string value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[^a-z0-9]+", "-");
        return text.Trim('-');
    }

    private static async Task WriteFileAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        const int maxAttempts = 10;
        var tempPath = path + ".tmp";

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await File.WriteAllTextAsync(tempPath, content, new UTF8Encoding(false), cancellationToken);
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Copy(tempPath, path, true);
                    File.Delete(tempPath);
                }
                else
                {
                    File.Move(tempPath, path);
                }

                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < maxAttempts)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken);
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter(StringBuilder builder) : base(builder)
        {
        }

        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
