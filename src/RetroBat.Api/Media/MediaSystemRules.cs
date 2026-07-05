namespace RetroBat.Api.Media;

public class MediaSystemRules
{
    private readonly MediaReferenceCatalog _referenceCatalog;
    private readonly Lazy<HashSet<string>> _arcadeSystems;
    private readonly Lazy<HashSet<string>> _folderSystems;
    private readonly Lazy<HashSet<string>> _crcNoCalculSystems;
    private readonly Lazy<HashSet<string>> _arcadeBiosNames;

    public MediaSystemRules(MediaReferenceCatalog referenceCatalog)
    {
        _referenceCatalog = referenceCatalog;
        _arcadeSystems = new Lazy<HashSet<string>>(() => LoadSemicolonList(_referenceCatalog.ArcadeSystemsPath));
        _folderSystems = new Lazy<HashSet<string>>(() => LoadLineList(_referenceCatalog.FolderSystemsPath));
        _crcNoCalculSystems = new Lazy<HashSet<string>>(() => LoadSemicolonAndLineList(_referenceCatalog.CrcNoCalculPath));
        _arcadeBiosNames = new Lazy<HashSet<string>>(() => LoadLineList(_referenceCatalog.RemoveArcadeBiosPath));
    }

    public bool IsArcadeLike(string? systemId)
    {
        return ContainsNormalized(_arcadeSystems.Value, systemId);
    }

    public bool IsFolderBasedSystem(string? systemId)
    {
        return ContainsNormalized(_folderSystems.Value, systemId);
    }

    public bool SkipCrcComputation(string? systemId, string? gamePath)
    {
        if (Directory.Exists(gamePath ?? string.Empty))
        {
            return true;
        }

        var normalized = NormalizeToken(systemId);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (_crcNoCalculSystems.Value.Contains(normalized))
        {
            return true;
        }

        var extension = Path.GetExtension(gamePath ?? string.Empty).ToLowerInvariant();
        return extension is ".m3u" or ".cue" or ".ccd" or ".img" or ".iso" or ".chd";
    }

    public bool IsFilteredArcadeBiosCandidate(string? systemId, string? gamePath)
    {
        if (!IsArcadeLike(systemId))
        {
            return false;
        }

        var name = Path.GetFileName(gamePath ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return _arcadeBiosNames.Value.Contains(name);
    }

    private static bool ContainsNormalized(HashSet<string> set, string? value)
    {
        var normalized = NormalizeToken(value);
        return !string.IsNullOrWhiteSpace(normalized) && set.Contains(normalized);
    }

    private static HashSet<string> LoadSemicolonList(string path)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return set;
        }

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            foreach (var token in trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = NormalizeToken(token);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    set.Add(normalized);
                }
            }
        }

        return set;
    }

    private static HashSet<string> LoadLineList(string path)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return set;
        }

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            var normalized = NormalizeToken(trimmed);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                set.Add(normalized);
            }
        }

        return set;
    }

    private static HashSet<string> LoadSemicolonAndLineList(string path)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return set;
        }

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            foreach (var token in trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = NormalizeToken(token);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    set.Add(normalized);
                }
            }
        }

        return set;
    }

    private static string NormalizeToken(string? value)
    {
        return (value ?? string.Empty).Trim().Replace(' ', '_').ToLowerInvariant();
    }
}
