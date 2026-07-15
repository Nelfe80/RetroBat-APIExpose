using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

/// <summary>
/// Deploys the curated per-game FBNeo RetroArch remaps
/// (resources\controls\retroarch\fbneo\rom.rmp) into
/// retroarch\config\remaps\FinalBurn Neo\rom.rmp. Conservative guard: a file the
/// user modified is kept ("kept-user-file"); identical content is "up-to-date".
/// FBNeo has no lamp outputs — this only covers the input side.
/// </summary>
public sealed class FbneoRmpDeployService
{
    private readonly PanelRemapExportService _remapExport;

    public FbneoRmpDeployService(PanelRemapExportService remapExport)
    {
        _remapExport = remapExport;
    }

    public sealed record Item(string Rom, string Status);

    public sealed record Report(int Total, int Written, int UpToDate, int Kept, int Failed, IReadOnlyList<Item> Changes, int PackTotal, int Offset);

    private static string PackDir => Path.Combine(RetroBatPaths.PluginRoot, "resources", "controls", "retroarch", "fbneo");

    private static string TargetDir => Path.Combine(RetroBatPaths.RetroBatRoot, "emulators", "retroarch", "config", "remaps", "FinalBurn Neo");

    public Report Deploy(string? rom = null, int offset = 0, int limit = 0)
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
            items.Count(i => i.Status == "up-to-date"),
            items.Count(i => i.Status == "kept-user-file"),
            items.Count(i => i.Status is "failed" or "missing"),
            items.Where(i => i.Status is not "up-to-date").ToList(),
            packRoms.Count,
            Math.Max(0, offset));
    }

    private Item DeployRom(string rom)
    {
        // preferred path: GENERATE the remap from the game dynpanel + cabinet
        // cartography (the pack copy is only a fallback for games without one)
        var generated = _remapExport.DeployFbneoGameRemap(rom);
        if (generated != "missing")
        {
            return new Item(rom, generated);
        }

        var packPath = Path.Combine(PackDir, rom + ".rmp");
        if (!File.Exists(packPath))
        {
            return new Item(rom, "missing");
        }

        try
        {
            var targetPath = Path.Combine(TargetDir, rom + ".rmp");
            var packText = File.ReadAllText(packPath);
            if (File.Exists(targetPath))
            {
                var targetText = File.ReadAllText(targetPath);
                if (Normalize(targetText) == Normalize(packText))
                {
                    return new Item(rom, "up-to-date");
                }

                // a differing file is the user's mapping: never clobbered
                return new Item(rom, "kept-user-file");
            }

            Directory.CreateDirectory(TargetDir);
            File.WriteAllText(targetPath, packText);
            return new Item(rom, "written");
        }
        catch
        {
            return new Item(rom, "failed");
        }
    }

    private static string Normalize(string text)
        => string.Join("\n", text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    private static string GamesDynpanelsDir => Path.Combine(RetroBatPaths.PluginRoot, "resources", "dynpanels", "games");

    /// <summary>Every game we can produce a remap for: generated from its dynpanel,
    /// plus the curated pack fallbacks.</summary>
    private static IReadOnlyList<string> ListPackRoms()
    {
        var roms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(GamesDynpanelsDir))
        {
            foreach (var name in Directory.EnumerateFiles(GamesDynpanelsDir, "*.json", SearchOption.AllDirectories)
                         .Select(Path.GetFileNameWithoutExtension))
            {
                if (!string.IsNullOrEmpty(name))
                {
                    roms.Add(name!);
                }
            }
        }

        if (Directory.Exists(PackDir))
        {
            foreach (var name in Directory.EnumerateFiles(PackDir, "*.rmp").Select(Path.GetFileNameWithoutExtension))
            {
                if (!string.IsNullOrEmpty(name))
                {
                    roms.Add(name!);
                }
            }
        }

        return roms.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
