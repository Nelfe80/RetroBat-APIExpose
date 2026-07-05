using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class ControlFilesCatalogService
{
    private static readonly IReadOnlyList<ControlFileDefinition> Definitions =
    [
        new(
            Id: "mame-cfg",
            Kind: "mame-cfg",
            Format: "mame-ctrlr-cfg",
            Runtime: "mame",
            SourceRelativeDirectory: Path.Combine("resources", "controls", "mame"),
            TargetDirectoryParts: ["saves", "mame", "ctrlr"],
            Extension: ".cfg"),
        new(
            Id: "retroarch-rmp-mame",
            Kind: "retroarch-rmp",
            Format: "retroarch-remap",
            Runtime: "retroarch-mame",
            SourceRelativeDirectory: Path.Combine("resources", "controls", "retroarch", "mame"),
            TargetDirectoryParts: ["emulators", "retroarch", "config", "remaps", "MAME"],
            Extension: ".rmp"),
        new(
            Id: "retroarch-rmp-fbneo",
            Kind: "retroarch-rmp",
            Format: "retroarch-remap",
            Runtime: "retroarch-fbneo",
            SourceRelativeDirectory: Path.Combine("resources", "controls", "retroarch", "fbneo"),
            TargetDirectoryParts: ["emulators", "retroarch", "config", "remaps", "FinalBurn Neo"],
            Extension: ".rmp"),
        new(
            Id: "retrobat-xml-mame",
            Kind: "retrobat-xml",
            Format: "retrobat-inputmapping-xml",
            Runtime: "retrobat-mame",
            SourceRelativeDirectory: Path.Combine("resources", "controls", "retrobat", "mame"),
            TargetDirectoryParts: ["user", "inputmapping", "mame"],
            Extension: ".xml")
    ];

    public PanelControlFilesSnapshot GetForRom(string? rom)
    {
        var normalizedRom = NormalizeRom(rom);
        return new PanelControlFilesSnapshot
        {
            Rom = normalizedRom,
            SourceRoot = ToApiPath(Path.Combine("resources", "controls")),
            Files = string.IsNullOrWhiteSpace(normalizedRom)
                ? []
                : Definitions.Select(definition => BuildEntry(normalizedRom, definition)).ToList()
        };
    }

    public bool TryGetFile(string? rom, string? id, out PanelControlFileEntry entry)
    {
        entry = new PanelControlFileEntry();

        var normalizedRom = NormalizeRom(rom);
        if (string.IsNullOrWhiteSpace(normalizedRom) || string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var definition = Definitions.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (definition == null)
        {
            return false;
        }

        entry = BuildEntry(normalizedRom, definition);
        return entry.ExistsInApi;
    }

    private static PanelControlFileEntry BuildEntry(string rom, ControlFileDefinition definition)
    {
        var fileName = rom + definition.Extension;
        var relativePath = Path.Combine(definition.SourceRelativeDirectory, fileName);
        var apiFilePath = Path.Combine(RetroBatPaths.PluginRoot, relativePath);
        var targetPath = Path.Combine(
            definition.TargetDirectoryParts.Aggregate(RetroBatPaths.RetroBatRoot, Path.Combine),
            fileName);
        var fileInfo = new FileInfo(apiFilePath);
        var targetInfo = new FileInfo(targetPath);

        return new PanelControlFileEntry
        {
            Id = definition.Id,
            Kind = definition.Kind,
            Format = definition.Format,
            Runtime = definition.Runtime,
            FileName = fileName,
            ApiPath = ToApiPath(relativePath),
            ApiFilePath = apiFilePath,
            TargetPath = targetPath,
            ExistsInApi = fileInfo.Exists,
            ExistsAtTarget = targetInfo.Exists,
            Length = fileInfo.Exists ? fileInfo.Length : null,
            LastWriteTimeUtc = fileInfo.Exists ? fileInfo.LastWriteTimeUtc : null,
            DownloadUrl = $"/api/v1/Panels/controls/{Uri.EscapeDataString(rom)}/files/{Uri.EscapeDataString(definition.Id)}/content"
        };
    }

    private static string NormalizeRom(string? rom)
    {
        if (string.IsNullOrWhiteSpace(rom))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileNameWithoutExtension(rom.Trim());
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalidChar.ToString(), string.Empty, StringComparison.Ordinal);
        }

        return fileName.Trim();
    }

    private static string ToApiPath(string path)
    {
        return path.Replace('\\', '/');
    }

    private sealed record ControlFileDefinition(
        string Id,
        string Kind,
        string Format,
        string Runtime,
        string SourceRelativeDirectory,
        IReadOnlyList<string> TargetDirectoryParts,
        string Extension);
}

public sealed class PanelControlFilesSnapshot
{
    public string Rom { get; set; } = string.Empty;
    public string SourceRoot { get; set; } = "resources/controls";
    public List<PanelControlFileEntry> Files { get; set; } = [];
}

public sealed class PanelControlFileEntry
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string Runtime { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ApiPath { get; set; } = string.Empty;
    public string ApiFilePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public bool ExistsInApi { get; set; }
    public bool ExistsAtTarget { get; set; }
    public long? Length { get; set; }
    public DateTime? LastWriteTimeUtc { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
}
