using System.Text.RegularExpressions;
using System.Xml.Linq;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Push deployment of the curated MAME cfg pack (resources/controls/mame) into
/// saves/mame/cfg. Never a blind copy: only the &lt;input&gt; ports present in the
/// pack are merged into the deployed file - MAME state (counters, DIP, mixer) is
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
    /// reads "Button 2" and B4 (x) reads "Button 3" - the DirectInput raw order
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
    /// <summary>Player 1 cabinet, for the inverse-wiring readback and P1 patches.</summary>
    private IReadOnlyDictionary<int, int> JoycodeByPhysical => JoycodeByPhysicalFor(1);

    private readonly Dictionary<int, IReadOnlyDictionary<int, int>> _joycodeByPlayerCache = new();

    /// <summary>
    /// physical button N → DirectInput JOYCODE button number, for ONE player's
    /// panel. Cabinets can wire their P1 and P2 panels/encoders differently, so
    /// JOYCODE_2 tokens are translated with player 2's cartography
    /// (CabinetButtonsByPlayer:2), falling back to the global map then the default.
    /// </summary>
    private IReadOnlyDictionary<int, int> JoycodeByPhysicalFor(int player)
    {
        if (_joycodeByPlayerCache.TryGetValue(player, out var cached))
        {
            return cached;
        }

        Dictionary<int, int> Build(string path)
        {
            var m = new Dictionary<int, int>();
            foreach (var child in _configuration.GetSection(path).GetChildren())
            {
                if (int.TryParse(child.Key, out var physical) && physical > 0
                    && child.Value is { } identity && StandardRawByIdentity.TryGetValue(identity.Trim(), out var raw))
                {
                    m[physical] = raw + 1;
                }
            }

            return m;
        }

        // read the per-player map from the appsettings FILE directly, so a freshly
        // written cartography is used at once (IConfiguration reload is async/stale)
        var map = new Dictionary<int, int>();
        foreach (var (physical, identity) in CabinetCartographyStore.Read(player))
        {
            if (int.TryParse(physical, out var n) && n > 0
                && StandardRawByIdentity.TryGetValue(identity.Trim(), out var raw))
            {
                map[n] = raw + 1;
            }
        }

        if (map.Count == 0)
        {
            map = Build("ApiExpose:PanelRemapExport:CabinetButtons");
        }

        if (map.Count == 0)
        {
            // measured default cabinet: 1:b 2:a 3:y 4:x 5:l2 6:r2 7:l 8:r
            map = new Dictionary<int, int> { [1] = 1, [2] = 2, [3] = 4, [4] = 3, [5] = 7, [6] = 8, [7] = 5, [8] = 6 };
        }

        _joycodeByPlayerCache[player] = map;
        return map;
    }

    /// <summary>Clears the per-player cabinet cache after the cartography changes.</summary>
    public void InvalidateCabinetCache()
    {
        _joycodeByPhysicalCache = null;
        _joycodeByPlayerCache.Clear();
    }

    /// <summary>Rewrites the pack's JOYCODE_x_BUTTONn tokens (n = physical slot,
    /// identity-authored) into the button numbers the cabinet really emits - using
    /// the cartography of the player the JOYCODE index refers to.</summary>
    private string TranslateJoycodes(string sequence)
    {
        return Regex.Replace(sequence, @"JOYCODE_(\d+)_BUTTON(\d+)", match =>
        {
            var joyIndex = int.TryParse(match.Groups[1].Value, out var ji) && ji >= 1 ? ji : 1;
            var physical = int.Parse(match.Groups[2].Value);
            return JoycodeByPhysicalFor(joyIndex).TryGetValue(physical, out var button)
                ? $"JOYCODE_{match.Groups[1].Value}_BUTTON{button}"
                : match.Value;
        });
    }

    /// <summary>
    /// Applies the cabinet translation to every input sequence of a pack document.
    /// Face/shoulder buttons (P{p}_BUTTON{k}) are PLACED from the game's dynpanel -
    /// the same authority the RetroArch rmp uses - so MAME matches the LEDs and
    /// RetroArch instead of the pack's own (X/Y-swapped) slot authoring. Every other
    /// port keeps the pack's slot, translated through the cartography as before.
    /// </summary>
    private void TranslatePackDocument(XDocument pack, string? rom = null)
    {
        var input = pack.Root?.Element("system")?.Element("input");
        if (input is null)
        {
            return;
        }

        var dynSlots = rom is null ? null : DynpanelButtonSlots(rom);

        foreach (var port in input.Elements("port"))
        {
            var seq = port.Element("newseq");
            if (seq is null)
            {
                continue;
            }

            var type = (string?)port.Attribute("type") ?? "";
            var buttonMatch = Regex.Match(type, @"^P(\d+)_BUTTON(\d+)$");
            if (dynSlots is not null && buttonMatch.Success
                && int.TryParse(buttonMatch.Groups[1].Value, out var player)
                && int.TryParse(buttonMatch.Groups[2].Value, out var button)
                && dynSlots.TryGetValue((player, button), out var slot)
                && JoycodeByPhysicalFor(player).TryGetValue(slot, out var joycodeButton))
            {
                // dynpanel-placed: rewrite this port's joystick token to the slot the
                // LED lights, keeping any keyboard/other tokens intact
                seq.Value = Regex.Replace(seq.Value, $@"JOYCODE_{player}_BUTTON\d+", $"JOYCODE_{player}_BUTTON{joycodeButton}");
                continue;
            }

            seq.Value = TranslateJoycodes(seq.Value);
        }

        AddSemanticSystemTokens(input);
    }

    /// <summary>
    /// Makes the ONE shared cfg (saves/mame/cfg/{rom}.cfg, read by BOTH mame64
    /// standalone AND the mame_libretro core) work in both backends for the system
    /// inputs that structurally diverge - Coin and Start.
    ///
    /// mame64 sees the cabinet encoder as a raw DirectInput device: its Coin/Start
    /// are physical buttons (JOYCODE_n_BUTTON10 / BUTTON9), no semantic token exists.
    /// mame_libretro presents a RetroPad: Coin/Start are the semantic JOYCODE_n_SELECT
    /// / JOYCODE_n_START, and the raw BUTTON9/BUTTON10 map onto L3/R3 (stick clicks,
    /// never wired on an arcade panel). So OR-ing both tokens is collision-free: each
    /// backend fires on its own valid token, the other stays inert. The pack ships the
    /// raw form (correct for mame64); this prepends the semantic token for libretro.
    /// </summary>
    private static void AddSemanticSystemTokens(XElement input)
    {
        foreach (var port in input.Elements("port"))
        {
            var type = (string?)port.Attribute("type") ?? "";
            var match = Regex.Match(type, @"^(COIN|START)(\d+)$");
            if (!match.Success)
            {
                continue;
            }

            var seq = port.Element("newseq");
            if (seq is null)
            {
                continue;
            }

            var semantic = match.Groups[1].Value == "COIN" ? "SELECT" : "START";
            // device index: the one the port already binds (JOYCODE_3 for P2 packs),
            // else the player number from the type - keep P1/P2 on their own device
            var deviceMatch = Regex.Match(seq.Value, @"JOYCODE_(\d+)_");
            var device = deviceMatch.Success ? deviceMatch.Groups[1].Value : match.Groups[2].Value;
            var token = $"JOYCODE_{device}_{semantic}";

            var tokens = SplitSeq(seq.Value);
            if (tokens.Any(t => t.Equals(token, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // already present (idempotent re-runs)
            }

            seq.Value = string.Join(" OR ", new[] { token }.Concat(tokens));
        }
    }

    /// <summary>
    /// Reads the game dynpanel (resources/dynpanels/games/{rom}.json) into a
    /// (player, MAME game-button k) → panel_slot map. This is the placement the LEDs
    /// and the rmp use; the MAME cfg is realigned onto it. Returns empty when the game
    /// has no dynpanel (its pack placement is then left untouched).
    /// </summary>
    private static IReadOnlyDictionary<(int Player, int Button), int> DynpanelButtonSlots(string rom)
    {
        var map = new Dictionary<(int, int), int>();
        var dir = Path.Combine(RetroBatPaths.PluginRoot, "resources", "dynpanels", "games");
        var path = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, rom + ".json", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (path is null)
        {
            return map;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("players", out var players))
            {
                return map;
            }

            foreach (var playerEntry in players.EnumerateObject())
            {
                if (!int.TryParse(playerEntry.Name, out var player)
                    || !playerEntry.Value.TryGetProperty("layouts", out var layouts))
                {
                    continue;
                }

                // prefer the 8-Button layout, else the first - the slot for a given
                // game button is the same across layouts for the X/Y realignment
                var layout = layouts.TryGetProperty("8-Button", out var eight)
                    ? eight
                    : layouts.EnumerateObject().Select(p => p.Value).FirstOrDefault();
                if (layout.ValueKind != System.Text.Json.JsonValueKind.Object
                    || !layout.TryGetProperty("buttons", out var buttons))
                {
                    continue;
                }

                foreach (var button in buttons.EnumerateObject())
                {
                    if (int.TryParse(button.Name, out var k)
                        && button.Value.TryGetProperty("panel_slot", out var ps)
                        && ps.TryGetInt32(out var slot))
                    {
                        map[(player, k)] = slot;
                    }
                }
            }
        }
        catch
        {
            // malformed dynpanel: leave the pack placement untouched
        }

        return map;
    }

    /// <summary>
    /// Current wiring of the DEPLOYED cfg, translated back to physical panel
    /// buttons: P1_BUTTONn → the cabinet buttons whose JOYCODEs appear in the
    /// port's standard sequence (inverse cartography). This is what really fires
    /// in MAME today - the patch bay shows it as the default cables.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> CurrentWiring(string rom)
    {
        var result = new Dictionary<string, IReadOnlyList<int>>(StringComparer.OrdinalIgnoreCase);
        if (!IsValidRomName(rom))
        {
            return result;
        }

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
    /// real port signatures, then realigns the cfg - wrong tag/mask/defvalue fixed,
    /// ports MAME silently dropped restored from the translated pack (+ overrides),
    /// user sequences kept, unknown ports removed. .bak written before saving.
    /// </summary>
    /// <summary>
    /// A MAME short name: lower-case letters, digits and underscore, 32 chars at most.
    /// Anything else (spaces, dashes, quotes, dots, separators) is refused.
    /// </summary>
    public static bool IsValidRomName(string? rom)
    {
        if (string.IsNullOrEmpty(rom) || rom.Length > 32)
        {
            return false;
        }

        foreach (var c in rom)
        {
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>MAME arguments of the headless port dump, one entry per argument.</summary>
    public static IReadOnlyList<string> RepairArguments(string rom, string luaScript) => new[]
    {
        rom,
        "-rompath",
        $"{Path.Combine(RetroBatPaths.RetroBatRoot, "roms", "mame")};{Path.Combine(RetroBatPaths.RetroBatRoot, "roms", "arcade")};{Path.Combine(RetroBatPaths.RetroBatRoot, "bios")}",
        "-autoboot_script",
        luaScript,
        "-video", "none",
        "-sound", "none",
        "-skip_gameinfo",
    };

    public RepairReport Repair(string rom)
    {
        rom = (rom ?? string.Empty).Trim().ToLowerInvariant();
        if (!IsValidRomName(rom))
        {
            // The name goes to the MAME command line and into file paths: a MAME
            // short name only, never options or path separators.
            return new RepairReport(rom.Length > 64 ? rom[..64] : rom, "failed", 0, 0, 0, new[] { "nom de rom invalide" });
        }

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
            WorkingDirectory = Path.GetDirectoryName(mameExe)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in RepairArguments(rom, luaScript))
        {
            psi.ArgumentList.Add(argument);
        }
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
            TranslatePackDocument(pack, rom);
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
                details.Add($"{type} : inconnu de cette version de MAME - retire");
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
    /// Deploys one rom's cfg, or the pack when rom is null - optionally a slice of
    /// it (offset/limit) so a client can chunk the run and show real progress.
    /// </summary>
    public Report Deploy(string? rom = null, int offset = 0, int limit = 0, bool replaceInputs = false)
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
                items.Add(DeployRom(entry, replaceInputs));
            }

            return new Report(
                items.Count,
                items.Count(i => i.Status == "written"),
                items.Count(i => i.Status is "merged" or "rewired"),
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

    private Item DeployRom(string rom, bool replaceInputs = false)
    {
        if (!IsValidRomName(rom))
        {
            return new Item(rom.Length > 64 ? rom[..64] : rom, "failed", "nom de rom invalide");
        }

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
                TranslatePackDocument(fresh, rom);
                ApplyInputPatches(fresh, inputPatches);
                fresh.Save(targetPath);
                return new Item(rom, "written", "deployed from pack");
            }

            var pack = XDocument.Load(packPath);
            var packRaw = XDocument.Load(packPath);
            TranslatePackDocument(pack, rom);
            var forcedTypes = ApplyInputPatches(pack, inputPatches);
            var target = XDocument.Load(targetPath);
            var changes = MergeInputPorts(pack, packRaw, target, forcedTypes, replaceInputs);
            if (changes == 0)
            {
                return new Item(rom, "up-to-date", "");
            }

            File.Copy(targetPath, targetPath + ".bak", overwrite: true);
            target.Save(targetPath);
            return new Item(rom, replaceInputs ? "rewired" : "merged", $"{changes} port(s)");
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
            // keep working) and OR-append the wired buttons - curator semantics.
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

    private static int MergeInputPorts(XDocument pack, XDocument packRaw, XDocument target, HashSet<string>? forcedTypes = null, bool replaceInputs = false)
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
            // of our earlier deployments (before the cabinet translation existed) -
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
                // A cartography change must reach panel-bound ports the normal merge
                // preserves: replace their joystick sequence with the freshly
                // translated pack form (keyboard-only ports below are untouched).
                if (replaceInputs && !TokensEqual(existingTokens, translatedTokens))
                {
                    existingSeq.Value = string.Join(" OR ", translatedTokens);
                    changes++;
                }

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
