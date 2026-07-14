using System.Text.Json.Nodes;
using RetroBat.Domain.Models;

namespace RetroBat.Api.Infrastructure;

public sealed class PanelDefinitionProjectionService
{
    private readonly ControlFilesCatalogService _controlFiles;

    public PanelDefinitionProjectionService(ControlFilesCatalogService controlFiles)
    {
        _controlFiles = controlFiles;
    }

    public PanelDefinitionProjection Build(PanelThemeSnapshot snapshot, PanelThemeLayoutSnapshot? activeLayout)
    {
        var sourceFile = ResolveEffectivePanelFile(snapshot);
        var root = LoadJson(sourceFile);
        var projection = new PanelDefinitionProjection
        {
            SourceFile = sourceFile,
            Scope = snapshot.Scope,
            SystemId = snapshot.SystemId,
            Rom = snapshot.Rom,
            Core = snapshot.Core,
            ActiveLayoutId = snapshot.ActiveLayoutId,
            ActiveLayoutType = activeLayout?.Type ?? string.Empty,
            ActiveLayoutName = activeLayout?.Name ?? string.Empty,
            ExportPlan = BuildExportPlan(snapshot.Rom)
        };

        for (var slot = 1; slot <= 8; slot++)
        {
            projection.Slots.Add(new PanelPhysicalSlotProjection { Slot = slot });
        }

        if (root == null)
        {
            return projection;
        }

        var players = root["players"] as JsonObject;
        if (players == null)
        {
            ApplyThemeFallback(projection, activeLayout);
            return projection;
        }

        var inputsByRef = new Dictionary<string, PanelControlInputProjection>(StringComparer.OrdinalIgnoreCase);
        var outputsById = new Dictionary<string, PanelControlOutputProjection>(StringComparer.OrdinalIgnoreCase);

        foreach (var playerEntry in players)
        {
            if (playerEntry.Value is not JsonObject playerObj)
            {
                continue;
            }

            var playerIndex = int.TryParse(playerEntry.Key, out var parsedPlayer) ? parsedPlayer : 0;
            CollectSystemInputs(projection, inputsByRef, playerObj, playerIndex);
            CollectDeviceInputs(projection, inputsByRef, playerObj, playerIndex);
            CollectButtons(projection, inputsByRef, playerObj, playerIndex, snapshot.ActiveLayoutId);
            CollectAxes(projection, inputsByRef, playerObj, playerIndex, snapshot.ActiveLayoutId);
            CollectOutputs(projection, outputsById, playerObj, playerIndex, snapshot.ActiveLayoutId);
        }

        foreach (var output in projection.ControlMap.Outputs)
        {
            output.ResolvedInputRef = ResolveInputRef(output.InputRef, inputsByRef);
        }

        projection.ExternalOutputs = projection.ControlMap.Outputs
            .Where(output => output.Slots.Count == 0)
            .ToList();
        projection.ExternalAxes = projection.ControlMap.Axes
            .Where(axis => axis.Slots.Count == 0 && axis.SlotsByPolarity.Count == 0)
            .ToList();

        return projection;
    }

    private static void CollectSystemInputs(
        PanelDefinitionProjection projection,
        Dictionary<string, PanelControlInputProjection> inputsByRef,
        JsonObject playerObj,
        int playerIndex)
    {
        var systemInputs = playerObj["system_inputs"] as JsonObject;
        if (systemInputs == null)
        {
            return;
        }

        foreach (var inputEntry in systemInputs)
        {
            if (inputEntry.Value is not JsonObject inputObj)
            {
                continue;
            }

            var input = BuildInput(
                id: inputEntry.Key,
                kind: "system-input",
                player: playerIndex,
                sourceObj: inputObj,
                slot: ReadSlotForLayout(inputObj, projection.ActiveLayoutId));
            projection.ControlMap.Inputs.Add(input);
            IndexInput(inputsByRef, input);
        }
    }

    private static void CollectDeviceInputs(
        PanelDefinitionProjection projection,
        Dictionary<string, PanelControlInputProjection> inputsByRef,
        JsonObject playerObj,
        int playerIndex)
    {
        var devices = playerObj["devices"] as JsonArray;
        if (devices == null)
        {
            return;
        }

        foreach (var deviceNode in devices.OfType<JsonObject>())
        {
            var deviceInputs = deviceNode["inputs"] as JsonObject;
            if (deviceInputs == null)
            {
                continue;
            }

            // the physical peripheral the input belongs to (joystick, spinner,
            // trackball, paddle…) — clients draw one node per device
            var deviceType = ReadString(deviceNode, "type");
            var deviceLabel = ReadString(deviceNode, "label");

            foreach (var inputEntry in deviceInputs)
            {
                if (inputEntry.Value is not JsonObject inputObj)
                {
                    continue;
                }

                var input = BuildInput(
                    id: inputEntry.Key,
                    kind: "device-input",
                    player: playerIndex,
                    sourceObj: inputObj,
                    slot: ReadSlotForLayout(inputObj, projection.ActiveLayoutId));
                input.DeviceType = deviceType;
                input.DeviceLabel = deviceLabel;
                projection.ControlMap.Inputs.Add(input);
                IndexInput(inputsByRef, input);
                AddInputToSlot(projection, input.Slot, input);
            }
        }
    }

    private static void CollectButtons(
        PanelDefinitionProjection projection,
        Dictionary<string, PanelControlInputProjection> inputsByRef,
        JsonObject playerObj,
        int playerIndex,
        string activeLayoutId)
    {
        var buttons = playerObj["buttons"] as JsonObject;
        var layoutButtons = playerObj["layouts"]?[activeLayoutId]?["buttons"] as JsonObject;
        if (buttons == null)
        {
            return;
        }

        foreach (var buttonEntry in buttons)
        {
            if (buttonEntry.Value is not JsonObject buttonObj)
            {
                continue;
            }

            var slot = ReadSlotForLayout(buttonObj, activeLayoutId)
                ?? layoutButtons?[buttonEntry.Key]?["panel_slot"]?.GetValue<int?>();
            var input = BuildInput(
                id: buttonEntry.Key,
                kind: "button",
                player: playerIndex,
                sourceObj: buttonObj,
                slot: slot);
            projection.ControlMap.Inputs.Add(input);
            IndexInput(inputsByRef, input);
            AddInputToSlot(projection, slot, input);
        }
    }

    private static void CollectAxes(
        PanelDefinitionProjection projection,
        Dictionary<string, PanelControlInputProjection> inputsByRef,
        JsonObject playerObj,
        int playerIndex,
        string activeLayoutId)
    {
        var axes = playerObj["axes"];
        if (axes == null)
        {
            return;
        }

        foreach (var axisObj in EnumerateControlObjects(axes, "axis"))
        {
            var axis = BuildAxis(axisObj.Key, playerIndex, axisObj.Value, activeLayoutId);
            projection.ControlMap.Axes.Add(axis);
            if (axis.Slots.Count > 0)
            {
                foreach (var slot in axis.Slots)
                {
                    AddAxisToSlot(projection, slot, axis);
                }
            }

            var inputAlias = new PanelControlInputProjection
            {
                Id = axis.Id,
                Kind = "axis",
                Player = axis.Player,
                Label = axis.Label,
                Function = axis.Function,
                Color = axis.Color,
                MameInput = axis.MameInput,
                MameTag = axis.MameTag,
                MameMask = axis.MameMask,
                MameDefValue = axis.MameDefValue
            };
            IndexInput(inputsByRef, inputAlias);
        }
    }

    private static void CollectOutputs(
        PanelDefinitionProjection projection,
        Dictionary<string, PanelControlOutputProjection> outputsById,
        JsonObject playerObj,
        int playerIndex,
        string activeLayoutId)
    {
        var systemOutputs = playerObj["system_outputs"];
        if (systemOutputs == null)
        {
            return;
        }

        foreach (var outputObj in EnumerateNamedObjects(systemOutputs))
        {
            var output = BuildOutput(outputObj.Key, playerIndex, outputObj.Value, activeLayoutId);
            if (outputsById.ContainsKey(output.Id))
            {
                continue;
            }

            outputsById[output.Id] = output;
            projection.ControlMap.Outputs.Add(output);
            foreach (var slot in output.Slots)
            {
                AddOutputToSlot(projection, slot, output);
            }
        }
    }

    private static PanelControlInputProjection BuildInput(string id, string kind, int player, JsonObject sourceObj, int? slot)
    {
        return new PanelControlInputProjection
        {
            Id = id,
            Kind = kind,
            Player = player,
            Slot = slot,
            Label = ReadString(sourceObj, "label", "logical_name", "game_button", "function"),
            Function = ReadString(sourceObj, "function"),
            Color = ReadString(sourceObj, "color"),
            RetropadId = sourceObj["mapping"]?["retropad_id"]?.GetValue<int?>()
                ?? sourceObj["retropad_id"]?.GetValue<int?>(),
            LibretroButton = ReadString(sourceObj["mapping"] as JsonObject, "libretro_button"),
            FbneoButton = ReadString(sourceObj["mapping"] as JsonObject, "fbneo_button"),
            MameInput = ReadString(sourceObj["mame"] as JsonObject, "input_id", "input", "type"),
            MameTag = ReadString(sourceObj["mame"] as JsonObject, "cfg_tag", "mame_tag", "tag"),
            MameMask = ReadString(sourceObj["mame"] as JsonObject, "mask_hex", "mask"),
            MameDefValue = ReadString(sourceObj["mame"] as JsonObject, "defvalue_hex", "defvalue"),
            JoystickWay = ReadString(sourceObj["mame"] as JsonObject, "joystick_way")
        };
    }

    private static PanelControlAxisProjection BuildAxis(string id, int player, JsonObject sourceObj, string activeLayoutId)
    {
        var axis = new PanelControlAxisProjection
        {
            Id = id,
            Kind = "axis",
            Player = player,
            Label = ReadString(sourceObj, "label", "logical_name", "function", "id"),
            Function = ReadString(sourceObj, "function"),
            Color = ReadString(sourceObj, "color"),
            MameInput = ReadString(sourceObj["mame"] as JsonObject, "input_id", "input", "type"),
            MameTag = ReadString(sourceObj["mame"] as JsonObject, "cfg_tag", "mame_tag", "tag"),
            MameMask = ReadString(sourceObj["mame"] as JsonObject, "mask_hex", "mask"),
            MameDefValue = ReadString(sourceObj["mame"] as JsonObject, "defvalue_hex", "defvalue")
        };

        var slot = ReadSlotForLayout(sourceObj, activeLayoutId);
        if (slot.HasValue)
        {
            axis.Slots.Add(slot.Value);
        }

        if (sourceObj["slots_by_polarity"] is JsonObject slotsByPolarity)
        {
            foreach (var polarity in slotsByPolarity)
            {
                var polaritySlot = ReadInt(polarity.Value);
                if (polaritySlot.HasValue)
                {
                    axis.SlotsByPolarity[polarity.Key] = polaritySlot.Value;
                }
            }
        }

        return axis;
    }

    private static PanelControlOutputProjection BuildOutput(string fallbackId, int player, JsonObject sourceObj, string activeLayoutId)
    {
        var id = ReadString(sourceObj, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            id = fallbackId;
        }

        var output = new PanelControlOutputProjection
        {
            Id = id,
            Kind = "output",
            Player = player,
            Name = ReadString(sourceObj, "name", "output_name"),
            Label = ReadString(sourceObj, "label", "name"),
            Function = ReadString(sourceObj, "function"),
            Color = ReadString(sourceObj, "color"),
            OutputName = ReadString(sourceObj, "output_name", "name"),
            ValueType = ReadString(sourceObj, "value_type"),
            InputRef = ReadString(sourceObj, "input_ref"),
            Group = ReadString(sourceObj, "group"),
            Usage = ReadString(sourceObj, "usage")
        };

        foreach (var slot in ReadSlotsForLayout(sourceObj, activeLayoutId))
        {
            output.Slots.Add(slot);
        }

        return output;
    }

    private static PanelResolvedInputRefProjection? ResolveInputRef(
        string inputRef,
        Dictionary<string, PanelControlInputProjection> inputsByRef)
    {
        if (string.IsNullOrWhiteSpace(inputRef))
        {
            return null;
        }

        var key = inputRef.Split('|', 2)[0].Trim();
        if (inputsByRef.TryGetValue(key, out var input))
        {
            return new PanelResolvedInputRefProjection
            {
                Raw = inputRef,
                Kind = input.Kind,
                Id = input.Id,
                Player = input.Player,
                Slot = input.Slot,
                Label = input.Label,
                MameInput = input.MameInput
            };
        }

        return new PanelResolvedInputRefProjection
        {
            Raw = inputRef,
            Kind = "unresolved",
            Id = key
        };
    }

    private static void IndexInput(Dictionary<string, PanelControlInputProjection> inputsByRef, PanelControlInputProjection input)
    {
        AddIndex(inputsByRef, input.Id, input);
        AddIndex(inputsByRef, input.MameInput, input);
        AddIndex(inputsByRef, input.Label, input);
        if (input.Kind.Equals("system-input", StringComparison.OrdinalIgnoreCase))
        {
            AddIndex(inputsByRef, "SYS " + input.Id.ToUpperInvariant(), input);
        }

        if (input.Kind.Equals("button", StringComparison.OrdinalIgnoreCase))
        {
            AddIndex(inputsByRef, input.Id.StartsWith('B') ? input.Id : "B" + input.Id, input);
        }
    }

    private static void AddIndex(Dictionary<string, PanelControlInputProjection> inputsByRef, string value, PanelControlInputProjection input)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            inputsByRef.TryAdd(value.Trim(), input);
        }
    }

    private static void AddInputToSlot(PanelDefinitionProjection projection, int? slot, PanelControlInputProjection input)
    {
        var target = FindSlot(projection, slot);
        if (target != null)
        {
            target.Inputs.Add(input);
        }
    }

    private static void AddOutputToSlot(PanelDefinitionProjection projection, int? slot, PanelControlOutputProjection output)
    {
        var target = FindSlot(projection, slot);
        if (target != null)
        {
            target.Outputs.Add(output);
        }
    }

    private static void AddAxisToSlot(PanelDefinitionProjection projection, int? slot, PanelControlAxisProjection axis)
    {
        var target = FindSlot(projection, slot);
        if (target != null)
        {
            target.Axes.Add(axis);
        }
    }

    private static PanelPhysicalSlotProjection? FindSlot(PanelDefinitionProjection projection, int? slot)
    {
        return slot is >= 1 and <= 8
            ? projection.Slots.FirstOrDefault(candidate => candidate.Slot == slot.Value)
            : null;
    }

    private PanelExportPlanProjection BuildExportPlan(string rom)
    {
        var controlFiles = _controlFiles.GetForRom(rom);
        return new PanelExportPlanProjection
        {
            Mode = "read-only",
            ControlFiles = controlFiles.Files
        };
    }

    private static void ApplyThemeFallback(PanelDefinitionProjection projection, PanelThemeLayoutSnapshot? activeLayout)
    {
        if (activeLayout == null)
        {
            return;
        }

        foreach (var player in activeLayout.Players)
        {
            foreach (var button in player.Buttons)
            {
                var input = new PanelControlInputProjection
                {
                    Id = button.Id,
                    Kind = "button",
                    Player = player.Index,
                    Slot = button.Slot,
                    Label = button.Label,
                    Function = button.Function,
                    Color = button.Color,
                    RetropadId = button.RetropadId,
                    LibretroButton = button.LibretroButton,
                    FbneoButton = button.FbneoButton,
                    MameInput = button.MameButton
                };
                projection.ControlMap.Inputs.Add(input);
                AddInputToSlot(projection, button.Slot, input);
            }
        }
    }

    private static IEnumerable<KeyValuePair<string, JsonObject>> EnumerateNamedObjects(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var entry in obj)
            {
                if (entry.Value is JsonObject value)
                {
                    yield return new KeyValuePair<string, JsonObject>(entry.Key, value);
                }
            }
        }
        else if (node is JsonArray array)
        {
            var index = 0;
            foreach (var value in array.OfType<JsonObject>())
            {
                var id = ReadString(value, "id", "name", "output_name");
                yield return new KeyValuePair<string, JsonObject>(string.IsNullOrWhiteSpace(id) ? index.ToString() : id, value);
                index++;
            }
        }
    }

    private static IEnumerable<KeyValuePair<string, JsonObject>> EnumerateControlObjects(JsonNode node, string fallbackPrefix)
    {
        if (node is JsonObject obj && LooksLikeControlObject(obj))
        {
            var id = ReadString(obj, "id", "input", "name", "output_name");
            yield return new KeyValuePair<string, JsonObject>(string.IsNullOrWhiteSpace(id) ? fallbackPrefix : id, obj);
            yield break;
        }

        foreach (var entry in EnumerateNamedObjects(node))
        {
            yield return entry;
        }
    }

    private static bool LooksLikeControlObject(JsonObject obj) =>
        obj.ContainsKey("mame") ||
        obj.ContainsKey("layouts") ||
        obj.ContainsKey("panel_slot") ||
        obj.ContainsKey("slots_by_layout") ||
        obj.ContainsKey("color");

    private static int? ReadSlotForLayout(JsonObject sourceObj, string activeLayoutId)
    {
        var direct = sourceObj["panel_slot"]?.GetValue<int?>();
        if (direct.HasValue)
        {
            return direct;
        }

        if (sourceObj["slots_by_layout"] is JsonObject slotsByLayout)
        {
            var slot = ReadInt(slotsByLayout[activeLayoutId]);
            if (slot.HasValue)
            {
                return slot;
            }
        }

        // system_outputs entries (lamps) carry their per-layout slot under
        // layouts[<activeLayoutId>].panel_slot rather than at the top level.
        if (sourceObj["layouts"] is JsonObject layouts && layouts[activeLayoutId] is JsonObject layoutEntry)
        {
            var layoutSlot = layoutEntry["panel_slot"]?.GetValue<int?>();
            if (layoutSlot.HasValue)
            {
                return layoutSlot;
            }
        }

        return null;
    }

    private static IReadOnlyList<int> ReadSlotsForLayout(JsonObject sourceObj, string activeLayoutId)
    {
        var slots = new List<int>();
        AddSlot(slots, ReadSlotForLayout(sourceObj, activeLayoutId));
        AddSlots(slots, sourceObj["panel_slots"]);

        if (sourceObj["slots_by_layout"] is JsonObject slotsByLayout)
        {
            AddSlots(slots, slotsByLayout[activeLayoutId]);
        }

        if (sourceObj["layouts"] is JsonObject layouts && layouts[activeLayoutId] is JsonObject layoutEntry)
        {
            AddSlot(slots, layoutEntry["panel_slot"]?.GetValue<int?>());
            AddSlots(slots, layoutEntry["panel_slots"]);
        }

        return slots.Distinct().ToArray();
    }

    private static void AddSlot(List<int> slots, int? slot)
    {
        if (slot is >= 1 and <= 8)
        {
            slots.Add(slot.Value);
        }
    }

    private static void AddSlots(List<int> slots, JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                AddSlot(slots, ReadInt(item));
            }

            return;
        }

        AddSlot(slots, ReadInt(node));
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node == null)
        {
            return null;
        }

        try
        {
            return node.GetValue<int?>();
        }
        catch
        {
            var value = node.ToString();
            return int.TryParse(value, out var parsed) ? parsed : null;
        }
    }

    private static string ReadString(JsonObject? obj, params string[] names)
    {
        if (obj == null)
        {
            return string.Empty;
        }

        foreach (var name in names)
        {
            var node = obj[name];
            if (node == null)
            {
                continue;
            }

            string value;
            try
            {
                value = node.GetValue<string>();
            }
            catch
            {
                value = node.ToString();
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static JsonObject? LoadJson(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
    }

    private static string ResolveEffectivePanelFile(PanelThemeSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.GamePanelFile) &&
            string.Equals(snapshot.Scope, "game", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.GamePanelFile;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.CorePanelFile) &&
            string.Equals(snapshot.Scope, "core", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot.CorePanelFile;
        }

        return snapshot.SystemPanelFile;
    }
}

public sealed class PanelDefinitionProjection
{
    public string SourceFile { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string SystemId { get; set; } = string.Empty;
    public string Rom { get; set; } = string.Empty;
    public string Core { get; set; } = string.Empty;
    public string ActiveLayoutId { get; set; } = string.Empty;
    public string ActiveLayoutType { get; set; } = string.Empty;
    public string ActiveLayoutName { get; set; } = string.Empty;
    public List<PanelPhysicalSlotProjection> Slots { get; set; } = [];
    public PanelControlMapProjection ControlMap { get; set; } = new();
    public List<PanelControlOutputProjection> ExternalOutputs { get; set; } = [];
    public List<PanelControlAxisProjection> ExternalAxes { get; set; } = [];
    public PanelExportPlanProjection ExportPlan { get; set; } = new();
}

public sealed class PanelPhysicalSlotProjection
{
    public int Slot { get; set; }
    public List<PanelControlInputProjection> Inputs { get; set; } = [];
    public List<PanelControlOutputProjection> Outputs { get; set; } = [];
    public List<PanelControlAxisProjection> Axes { get; set; } = [];
}

public sealed class PanelControlMapProjection
{
    public List<PanelControlInputProjection> Inputs { get; set; } = [];
    public List<PanelControlOutputProjection> Outputs { get; set; } = [];
    public List<PanelControlAxisProjection> Axes { get; set; } = [];
}

public sealed class PanelControlInputProjection
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Player { get; set; }
    public int? Slot { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Function { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public int? RetropadId { get; set; }
    public string LibretroButton { get; set; } = string.Empty;
    public string FbneoButton { get; set; } = string.Empty;
    public string MameInput { get; set; } = string.Empty;
    public string MameTag { get; set; } = string.Empty;
    public string MameMask { get; set; } = string.Empty;
    public string MameDefValue { get; set; } = string.Empty;

    /// <summary>Joystick granularity from the dynpanel mame block ("2", "4", "8",
    /// "vertical2"…), so clients can draw the right number of anchors.</summary>
    public string JoystickWay { get; set; } = string.Empty;

    /// <summary>Physical peripheral carrying this input (joystick, spinner,
    /// trackball, paddle, pedal…), from the dynpanel device list.</summary>
    public string DeviceType { get; set; } = string.Empty;

    public string DeviceLabel { get; set; } = string.Empty;
}

public sealed class PanelControlOutputProjection
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Player { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Function { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string OutputName { get; set; } = string.Empty;
    public string ValueType { get; set; } = string.Empty;
    public string InputRef { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
    public string Usage { get; set; } = string.Empty;
    public PanelResolvedInputRefProjection? ResolvedInputRef { get; set; }
    public List<int> Slots { get; set; } = [];
}

public sealed class PanelControlAxisProjection
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int Player { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Function { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string MameInput { get; set; } = string.Empty;
    public string MameTag { get; set; } = string.Empty;
    public string MameMask { get; set; } = string.Empty;
    public string MameDefValue { get; set; } = string.Empty;
    public List<int> Slots { get; set; } = [];
    public Dictionary<string, int> SlotsByPolarity { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PanelResolvedInputRefProjection
{
    public string Raw { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public int Player { get; set; }
    public int? Slot { get; set; }
    public string Label { get; set; } = string.Empty;
    public string MameInput { get; set; } = string.Empty;
}

public sealed class PanelExportPlanProjection
{
    public string Mode { get; set; } = "read-only";
    public List<PanelControlFileEntry> ControlFiles { get; set; } = [];
}
