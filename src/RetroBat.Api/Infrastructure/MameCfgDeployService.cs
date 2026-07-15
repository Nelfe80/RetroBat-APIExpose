using System.Text.RegularExpressions;
using System.Xml.Linq;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Push deployment of the curated MAME cfg pack (resources/controls/mame) into
/// saves/mame/cfg. Never a blind copy: only the &lt;input&gt; ports present in the
/// pack are merged into the deployed file — MAME state (counters, DIP, mixer) is
/// kept, and the user's manual binds are preserved with the pack's JOYCODE forms
/// OR-appended (KEYCODE_Z becomes "KEYCODE_Z OR JOYCODE_1_BUTTON1"). Callers must
/// refuse to deploy while MAME runs: MAME rewrites every cfg at exit.
/// </summary>
public sealed class MameCfgDeployService
{
    public sealed record Item(string Rom, string Status, string Detail);

    public sealed record Report(int Total, int Written, int Merged, int UpToDate, int Failed, IReadOnlyList<Item> Changes, int PackTotal, int Offset);

    private static string PackDir => Path.Combine(RetroBatPaths.PluginRoot, "resources", "controls", "mame");

    private static string TargetDir => Path.Combine(RetroBatPaths.RetroBatRoot, "saves", "mame", "cfg");

    /// <summary>RetroPad identity name → raw DirectInput index on the standard
    /// encoder profile (raw N = MAME JOYCODE_BUTTON(N+1)).
    /// Measured 2026-07-14 via MAME TAB (0-based display): physical B3 (identity y)
    /// reads "Button 2" and B4 (x) reads "Button 3" — the DirectInput raw order
    /// keeps x/y in their identity slots, unlike the SDL chain; only the shoulder
    /// pairs swap (B6→BUTTON8, B8→BUTTON6, B5→BUTTON7, B7→BUTTON5).</summary>
    private static readonly IReadOnlyDictionary<string, int> StandardRawByIdentity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["b"] = 0, ["a"] = 1, ["y"] = 2, ["x"] = 3, ["l"] = 4, ["r"] = 5, ["l2"] = 6, ["r2"] = 7
    };

    private readonly IConfiguration _configuration;
    private readonly object _deployLock = new();
    private IReadOnlyDictionary<int, int>? _joycodeByPhysicalCache;

    public MameCfgDeployService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// physical button N → MAME JOYCODE button number, derived from the SAME
    /// cabinet cartography as the RetroArch remaps (CabinetButtons in appsettings):
    /// the pack's JOYCODE values are authored under the identity assumption
    /// (slot N = BUTTONN), so deployment translates them to what the cabinet's
    /// buttons really emit over DirectInput (e.g. B6 emits BUTTON8 on the
    /// standard wiring, not BUTTON6).
    /// </summary>
    private IReadOnlyDictionary<int, int> JoycodeByPhysical
    {
        get
        {
            if (_joycodeByPhysicalCache is not null)
            {
                return _joycodeByPhysicalCache;
            }

            var map = new Dictionary<int, int>();
            foreach (var child in _configuration.GetSection("ApiExpose:PanelRemapExport:CabinetButtons").GetChildren())
            {
                if (int.TryParse(child.Key, out var physical) && physical > 0
                    && child.Value is { } identity && StandardRawByIdentity.TryGetValue(identity.Trim(), out var raw))
                {
                    map[physical] = raw + 1;
                }
            }

            if (map.Count == 0)
            {
                // measured default cabinet: 1:b 2:a 3:y 4:x 5:l2 6:r2 7:l 8:r
                map = new Dictionary<int, int> { [1] = 1, [2] = 2, [3] = 4, [4] = 3, [5] = 7, [6] = 8, [7] = 5, [8] = 6 };
            }

            return _joycodeByPhysicalCache = map;
        }
    }

    /// <summary>Rewrites the pack's JOYCODE_x_BUTTONn tokens (n = physical slot,
    /// identity-authored) into the button numbers the cabinet really emits.</summary>
    private string TranslateJoycodes(string sequence)
    {
        return Regex.Replace(sequence, @"JOYCODE_(\d+)_BUTTON(\d+)", match =>
        {
            var physical = int.Parse(match.Groups[2].Value);
            return JoycodeByPhysical.TryGetValue(physical, out var button)
                ? $"JOYCODE_{match.Groups[1].Value}_BUTTON{button}"
                : match.Value;
        });
    }

    /// <summary>Applies TranslateJoycodes to every input sequence of a pack document.</summary>
    private void TranslatePackDocument(XDocument pack)
    {
        var input = pack.Root?.Element("system")?.Element("input");
        if (input is null)
        {
            return;
        }

        foreach (var seq in input.Elements("port").Select(p => p.Element("newseq")).Where(s => s is not null))
        {
            seq!.Value = TranslateJoycodes(seq.Value);
        }
    }

    /// <summary>
    /// Current wiring of the DEPLOYED cfg, translated back to physical panel
    /// buttons: P1_BUTTONn → the cabinet buttons whose JOYCODEs appear in the
    /// port's standard sequence (inverse cartography). This is what really fires
    /// in MAME today — the patch bay shows it as the default cables.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> CurrentWiring(string rom)
    {
        var result = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(TargetDir, rom.Trim().ToLowerInvariant() + ".cfg");
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            var physicalByJoycode = JoycodeByPhysical.ToDictionary(pair => pair.Value, pair => pair.Key);
            foreach (var port in XDocument.Load(path).Descendants("port"))
            {
                var type = (string?)port.Attribute("type");
                if (type is null || !Regex.IsMatch(type, @"^P\d+_BUTTON\d+$"))
                {
                    continue;
                }

                var seq = port.Elements("newseq").FirstOrDefault(n => (string?)n.Attribute("type") == "standard");
                if (seq is null)
                {
                    continue;
                }

                var slots = Regex.Matches(seq.Value, @"JOYCODE_\d+_BUTTON(\d+)")
                    .Select(match => int.Parse(match.Groups[1].Value))
                    .Where(physicalByJoycode.ContainsKey)
                    .Select(button => physicalByJoycode[button])
                    .Distinct()
                    .OrderBy(slot => slot)
                    .ToArray();
                if (slots.Length > 0)
                {
                    result[type] = slots;
                }
            }
        }
        catch
        {
            // unreadable cfg: the caller falls back to the template defaults
        }

        return result;
    }

    public sealed record RepairReport(string Rom, string Status, int Realigned, int Restored, int Removed, IReadOnlyList<string> Details);

    /// <summary>
    /// Repairs a deployed cfg against the INSTALLED MAME: boots the game headless
    /// with the vendored dump_ports.lua (LedManager\tools\mame-repair) to read the
    /// real port signatures, then realigns the cfg — wrong tag/mask/defvalue fixed,
    /// ports MAME silently dropped restored from the translated pack (+ overrides),
    /// user sequences kept, unknown ports removed. .bak written before saving.
    /// </summary>
    public RepairReport Repair(string rom)
    {
        rom = rom.Trim().ToLowerInvariant();
        var details = new List<string>();
        var mameExe = Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "mame", "mame.exe");
        var luaScript = Path.GetFullPath(Path.Combine(RetroBatPaths.PluginRoot, "..", "LedManager", "tools", "mame-repair", "dump_ports.lua"));
        if (!File.Exists(mameExe))
        {
            return new RepairReport(rom, "failed", 0, 0, 0, new[] { "mame.exe introuvable (emulators\\mame)" });
        }

        if (!File.Exists(luaScript))
        {
            return new RepairReport(rom, "failed", 0, 0, 0, new[] { "dump_ports.lua introuvable (LedManager\\tools\\mame-repair)" });
        }

        // 1) ground truth: real port signatures of the installed MAME
        var dumpPath = Path.Combine(Path.GetTempPath(), $"ledmanager_ports_{rom}.txt");
        try
        {
            File.Delete(dumpPath);
        }
        catch
        {
            // stale dump: overwritten below anyway
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = mameExe,
            Arguments = $"{rom} -rompath \"{Path.Combine(RetroBatPaths.RetroBatRoot, "roms", "mame")};{Path.Combine(RetroBatPaths.RetroBatRoot, "roms", "arcade")};{Path.Combine(RetroBatPaths.RetroBatRoot, "bios")}\" -autoboot_script \"{luaScript}\" -video none -sound none -skip_gameinfo",
            WorkingDirectory = Path.GetDirectoryName(mameExe)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.EnvironmentVariables["LEDMANAGER_PORTS_OUT"] = dumpPath;
        using (var process = System.Diagnostics.Process.Start(psi))
        {
            if (process is null || !process.WaitForExit(90_000))
            {
                try
                {
                    process?.Kill(entireProcessTree: true);
                }
                catch
                {
                    // best effort
                }

                return new RepairReport(rom, "failed", 0, 0, 0, new[] { "MAME n'a pas termine le diagnostic (90 s)" });
            }
        }

        if (!File.Exists(dumpPath))
        {
            return new RepairReport(rom, "failed", 0, 0, 0, new[] { "diagnostic vide : la rom demarre-t-elle dans MAME ?" });
        }

        var truth = new Dictionary<string, (string Tag, int Mask, int DefValue)>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(dumpPath))
        {
            var parts = line.Split('|');
            if (parts.Length == 4 && int.TryParse(parts[2], out var mask) && int.TryParse(parts[3], out var defValue)
                && !truth.ContainsKey(parts[1]))
            {
                truth[parts[1]] = (parts[0], mask, defValue);
            }
        }

        if (truth.Count == 0)
        {
            return new RepairReport(rom, "failed", 0, 0, 0, new[] { "aucun port lu dans le diagnostic" });
        }

        // 2) desired sequences: current deployed seqs first (user), else the
        // translated pack + patch-bay overrides
        var targetPath = Path.Combine(TargetDir, rom + ".cfg");
        var packPath = Path.Combine(PackDir, rom + ".cfg");
        var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Harvest(XDocument? doc, bool overwrite)
        {
            var input = doc?.Root?.Element("system")?.Element("input");
            if (input is null)
            {
                return;
            }

            foreach (var port in input.Elements("port"))
            {
                var type = (string?)port.Attribute("type");
                var seq = port.Elements("newseq").FirstOrDefault(n => (string?)n.Attribute("type") == "standard")?.Value?.Trim();
                if (type is null || string.IsNullOrWhiteSpace(seq))
                {
                    continue;
                }

                if (overwrite || !desired.ContainsKey(type))
                {
                    desired[type] = seq!;
                }
            }
        }

        if (File.Exists(packPath))
        {
            var pack = XDocument.Load(packPath);
            TranslatePackDocument(pack);
            ApplyInputPatches(pack, LoadInputPatches(rom));
            Harvest(pack, overwrite: false);
        }

        var current = File.Exists(targetPath) ? XDocument.Load(targetPath) : null;
        Harvest(current, overwrite: true); // the user's live cfg wins

        // 3) rebuild the input section on the TRUE signatures
        var realigned = 0;
        var restored = 0;
        var removed = 0;
        var newInput = new XElement("input");
        foreach (var (type, seq) in desired.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!truth.TryGetValue(type, out var signature))
            {
                removed++;
                details.Add($"{type} : inconnu de cette version de MAME — retire");
                continue;
            }

            var existed = current?.Root?.Element("system")?.Element("input")?.Elements("port")
                .Any(p => (string?)p.Attribute("type") == type
                          && (string?)p.Attribute("tag") == signature.Tag
                          && (string?)p.Attribute("mask") == signature.Mask.ToString()) == true;
            if (existed)
            {
                realigned++; // identical signature: kept as-is
            }
            else
            {
                restored++;
                details.Add($"{type} : signature realignee ({signature.Tag} mask {signature.Mask})");
            }

            newInput.Add(new XElement("port",
                new XAttribute("tag", signature.Tag),
                new XAttribute("type", type),
                new XAttribute("mask", signature.Mask),
                new XAttribute("defvalue", signature.DefValue),
                new XElement("newseq", new XAttribute("type", "standard"), seq)));
        }

        if (newInput.Elements().Any())
        {
            var doc = current ?? new XDocument(new XElement("mameconfig", new XAttribute("version", "10"),
                new XElement("system", new XAttribute("name", rom))));
            var system = doc.Root!.Element("system")!;
            system.Element("input")?.Remove();
            system.AddFirst(newInput);
            if (File.Exists(targetPath))
            {
                File.Copy(targetPath, targetPath + ".bak", overwrite: true);
            }

            Directory.CreateDirectory(TargetDir);
            doc.Save(targetPath);
        }

        return new RepairReport(rom, restored + removed > 0 ? "repaired" : "up-to-date", realigned, restored, removed, details);
    }

    public bool IsMameRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcesses()
                .Any(p => p.ProcessName.StartsWith("mame", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Deploys one rom's cfg, or the pack when rom is null — optionally a slice of
    /// it (offset/limit) so a client can chunk the run and show real progress.
    /// </summary>
    public Report Deploy(string? rom = null, int offset = 0, int limit = 0)
    {
        lock (_deployLock)
        {
            var packRoms = ListPackRoms();
            IReadOnlyList<string> roms;
            if (!string.IsNullOrWhiteSpace(rom))
            {
                roms = new[] { rom.Trim().ToLowerInvariant() };
                offset = 0;
            }
            else
            {
                var slice = packRoms.Skip(Math.Max(0, offset));
                if (limit > 0)
                {
                    slice = slice.Take(limit);
                }

                roms = slice.ToArray();
            }

            var items = new List<Item>();
            foreach (var entry in roms)
            {
                items.Add(DeployRom(entry));
            }

            return new Report(
                items.Count,
                items.Count(i => i.Status == "written"),
                items.Count(i => i.Status == "merged"),
                items.Count(i => i.Status == "up-to-date"),
                items.Count(i => i.Status is "failed" or "missing"),
                items.Where(i => i.Status != "up-to-date").ToList(),
                packRoms.Count,
                Math.Max(0, offset));
        }
    }

    private static IReadOnlyList<string> ListPackRoms()
    {
        if (!Directory.Exists(PackDir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(PackDir, "*.cfg")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private Item DeployRom(string rom)
    {
        var packPath = Path.Combine(PackDir, rom + ".cfg");
        if (!File.Exists(packPath))
        {
            return new Item(rom, "missing", "not in resources/controls/mame");
        }

        var targetPath = Path.Combine(TargetDir, rom + ".cfg");
        try
        {
            var inputPatches = LoadInputPatches(rom);

            if (!File.Exists(targetPath))
            {
                Directory.CreateDirectory(TargetDir);
                var fresh = XDocument.Load(packPath);
                TranslatePackDocument(fresh);
                ApplyInputPatches(fresh, inputPatches);
                fresh.Save(targetPath);
                return new Item(rom, "written", "deployed from pack");
            }

            var pack = XDocument.Load(packPath);
            var packRaw = XDocument.Load(packPath);
            TranslatePackDocument(pack);
            var forcedTypes = ApplyInputPatches(pack, inputPatches);
            var target = XDocument.Load(targetPath);
            var changes = MergeInputPorts(pack, packRaw, target, forcedTypes);
            if (changes == 0)
            {
                return new Item(rom, "up-to-date", "");
            }

            File.Copy(targetPath, targetPath + ".bak", overwrite: true);
            target.Save(targetPath);
            return new Item(rom, "merged", $"{changes} port(s)");
        }
        catch (Exception ex)
        {
            return new Item(rom, "failed", ex.Message);
        }
    }

    /// <summary>
    /// Merges the pack's input ports into the deployed document. Returns how many
    /// ports were added or updated. Existing manual sequences come first, the pack
    /// tokens are OR-appended; a cleared "NONE" sequence is replaced outright.
    /// </summary>
    /// <summary>
    /// Input-channel patches from the LedManagerSetup patch bay
    /// (LedManager\overrides\games\{arcade|mame}\rom.json, "inputs" section):
    /// mame input id → panel buttons that trigger it (multi-allocation).
    /// </summary>
    private static Dictionary<string, List<int>> LoadInputPatches(string rom)
    {
        var patches = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var system in new[] { "arcade", "mame" })
        {
            var path = Path.GetFullPath(Path.Combine(RetroBatPaths.PluginRoot, "..", "LedManager", "overrides", "games", system, rom + ".json"));
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (!doc.RootElement.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != System.Text.Json.JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var entry in inputs.EnumerateObject())
                {
                    if (entry.Value.TryGetProperty("slots", out var slots) && slots.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var parsed = slots.EnumerateArray()
                            .Where(s => s.TryGetInt32(out var v) && v >= 1)
                            .Select(s => s.GetInt32())
                            .ToList();
                        if (parsed.Count > 0 && !patches.ContainsKey(entry.Name))
                        {
                            patches[entry.Name] = parsed;
                        }
                    }
                }
            }
            catch
            {
                // malformed override: ignored
            }

            break; // first existing file wins (same convention as the runtime store)
        }

        return patches;
    }

    /// <summary>
    /// Rewrites the patched ports' sequences: pack KEYCODEs kept, JOYCODEs replaced
    /// by the OR of every wired button (translated through the cabinet cartography).
    /// Returns the port types whose content is now user-authoritative.
    /// </summary>
    private HashSet<string> ApplyInputPatches(XDocument pack, Dictionary<string, List<int>> patches)
    {
        var forced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (patches.Count == 0)
        {
            return forced;
        }

        var input = pack.Root?.Element("system")?.Element("input");
        if (input is null)
        {
            return forced;
        }

        foreach (var port in input.Elements("port"))
        {
            var type = (string?)port.Attribute("type");
            if (type is null || !patches.TryGetValue(type, out var slots))
            {
                continue;
            }

            var seq = port.Element("newseq");
            if (seq is null)
            {
                continue;
            }

            var tokens = SplitSeq(seq.Value);
            var joyIndex = tokens.Select(t => Regex.Match(t, @"^JOYCODE_(\d+)_"))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value)
                .FirstOrDefault() ?? "1";

            // game buttons: replace the joycodes with the wired buttons.
            // directions/others (P1_LEFT…): KEEP the axis switches (the stick must
            // keep working) and OR-append the wired buttons — curator semantics.
            var isGameButton = Regex.IsMatch(type, @"^P\d+_BUTTON\d+$");
            var kept = tokens.Where(t =>
                !t.StartsWith("JOYCODE_", StringComparison.OrdinalIgnoreCase)
                || (!isGameButton && !Regex.IsMatch(t, @"^JOYCODE_\d+_BUTTON\d+$")));
            var joycodes = slots
                .Where(s => JoycodeByPhysical.ContainsKey(s))
                .Select(s => $"JOYCODE_{joyIndex}_BUTTON{JoycodeByPhysical[s]}");
            seq.Value = string.Join(" OR ", kept.Concat(joycodes).Distinct(StringComparer.OrdinalIgnoreCase));
            forced.Add(type);
        }

        return forced;
    }

    private static int MergeInputPorts(XDocument pack, XDocument packRaw, XDocument target, HashSet<string>? forcedTypes = null)
    {
        var packInput = pack.Root?.Element("system")?.Element("input");
        var packRawInput = packRaw.Root?.Element("system")?.Element("input");
        var targetSystem = target.Root?.Element("system");
        if (packInput is null || targetSystem is null)
        {
            return 0;
        }

        var targetInput = targetSystem.Element("input");
        if (targetInput is null)
        {
            targetSystem.AddFirst(new XElement(packInput));
            return packInput.Elements("port").Count();
        }

        var changes = 0;
        foreach (var packPort in packInput.Elements("port"))
        {
            var type = (string?)packPort.Attribute("type");
            if (string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            var tag = (string?)packPort.Attribute("tag");
            var existing = targetInput.Elements("port").FirstOrDefault(p =>
                string.Equals((string?)p.Attribute("type"), type, StringComparison.Ordinal) &&
                string.Equals((string?)p.Attribute("tag"), tag, StringComparison.Ordinal));

            if (existing is null)
            {
                targetInput.Add(new XElement(packPort));
                changes++;
                continue;
            }

            var packSeq = packPort.Element("newseq");
            if (packSeq is null)
            {
                continue;
            }

            var existingSeq = existing.Element("newseq");
            if (existingSeq is null)
            {
                existing.Add(new XElement(packSeq));
                changes++;
                continue;
            }

            var existingTokens = SplitSeq(existingSeq.Value);
            var translatedTokens = SplitSeq(packSeq.Value);

            // Patch-bay multi-allocations are the user's LATEST intent: they replace
            // whatever is deployed, bypassing every preservation rule below.
            if (forcedTypes is not null && forcedTypes.Contains(type))
            {
                if (!TokensEqual(existingTokens, translatedTokens))
                {
                    existingSeq.Value = string.Join(" OR ", translatedTokens);
                    changes++;
                }

                continue;
            }

            // Self-recognition: a sequence identical to the RAW pack content is one
            // of our earlier deployments (before the cabinet translation existed) —
            // upgrade it outright to the translated form.
            var rawSeq = FindSeq(packRawInput, tag, type);
            if (rawSeq is not null && TokensEqual(existingTokens, SplitSeq(rawSeq)))
            {
                if (!TokensEqual(existingTokens, translatedTokens))
                {
                    existingSeq.Value = string.Join(" OR ", translatedTokens);
                    changes++;
                }

                continue;
            }

            // A deliberate NONE (cleared by the user) stays dead, and a port already
            // bound to any JOYCODE is panel-functional as the user arranged it: both
            // are personal choices the deployment must respect. Only keyboard-only
            // manual configs get the pack's panel forms OR-appended.
            if (existingTokens.Count == 1 && existingTokens[0].Equals("NONE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (existingTokens.Any(t => t.StartsWith("JOYCODE_", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var merged = existingTokens.ToList();
            foreach (var token in translatedTokens)
            {
                if (!merged.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    merged.Add(token);
                }
            }

            if (merged.Count != existingTokens.Count)
            {
                existingSeq.Value = string.Join(" OR ", merged);
                changes++;
            }
        }

        changes += EnsureJoystickJoycodes(targetInput);
        return changes;
    }

    /// <summary>
    /// Reactivates the stick on direction ports that a manual keyboard-only
    /// configuration overrode: a port like P1_JOYSTICK_LEFT bound to KEYCODE_LEFT
    /// alone hides MAME's default joystick binding, so the panel stick goes dead.
    /// The legacy axis form (JOYCODE_p_XAXIS_LEFT_SWITCH…) is OR-appended, the
    /// keyboard binding is kept. Ports deliberately cleared (NONE) are respected.
    /// </summary>
    private static int EnsureJoystickJoycodes(XElement targetInput)
    {
        var changes = 0;
        foreach (var port in targetInput.Elements("port"))
        {
            var type = (string?)port.Attribute("type");
            if (string.IsNullOrWhiteSpace(type))
            {
                continue;
            }

            var match = Regex.Match(type, @"^P(\d+)_JOYSTICK_(UP|DOWN|LEFT|RIGHT)$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                continue;
            }

            var seq = port.Element("newseq");
            if (seq is null)
            {
                continue;
            }

            var tokens = SplitSeq(seq.Value);
            if (tokens.Count == 0
                || tokens.Any(t => t.StartsWith("JOYCODE_", StringComparison.OrdinalIgnoreCase))
                || (tokens.Count == 1 && tokens[0].Equals("NONE", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var player = match.Groups[1].Value;
            var direction = match.Groups[2].Value.ToUpperInvariant();
            var axis = direction is "LEFT" or "RIGHT" ? "XAXIS" : "YAXIS";
            seq.Value = string.Join(" OR ", tokens.Append($"JOYCODE_{player}_{axis}_{direction}_SWITCH"));
            changes++;
        }

        return changes;
    }

    private static string? FindSeq(XElement? input, string? tag, string type)
    {
        return input?.Elements("port")
            .FirstOrDefault(p => string.Equals((string?)p.Attribute("type"), type, StringComparison.Ordinal)
                                 && string.Equals((string?)p.Attribute("tag"), tag, StringComparison.Ordinal))
            ?.Element("newseq")?.Value;
    }

    private static bool TokensEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        return left.Count == right.Count
               && left.All(token => right.Contains(token, StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> SplitSeq(string value)
    {
        return Regex.Split(value, @"\s+OR\s+")
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .ToArray();
    }
}
