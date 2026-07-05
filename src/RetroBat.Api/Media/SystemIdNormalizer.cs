namespace RetroBat.Api.Media;

public class SystemIdNormalizer
{
    private readonly Lazy<Dictionary<string, string>> _arrmMap;
    private readonly MediaReferenceCatalog _referenceCatalog;

    public SystemIdNormalizer(MediaReferenceCatalog referenceCatalog)
    {
        _referenceCatalog = referenceCatalog;
        _arrmMap = new Lazy<Dictionary<string, string>>(LoadSystemMap);
    }

    public string Normalize(string? systemId)
    {
        var normalized = NormalizeFrontend(systemId);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (_arrmMap.Value.ContainsKey(normalized))
        {
            if (normalized is "mame" or "fbneo" or "fba" or "hbmame")
            {
                return "arcade";
            }

            return normalized switch
            {
                "atarijaguar" => "jaguar",
                "atarijaguarcd" => "jaguarcd",
                "atarilynx" => "lynx",
                _ => normalized
            };
        }

        return normalized;
    }

    public string NormalizeFrontend(string? systemId)
    {
        var candidate = (systemId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return string.Empty;
        }

        return candidate.Replace(' ', '_').ToLowerInvariant();
    }

    public string ResolveScreenScraperSystemId(string? systemId)
    {
        var normalized = NormalizeFrontend(systemId);
        return !string.IsNullOrWhiteSpace(normalized) &&
            _arrmMap.Value.TryGetValue(normalized, out var screenScraperSystemId)
            ? screenScraperSystemId
            : string.Empty;
    }

    private Dictionary<string, string> LoadSystemMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var jsonPath = _referenceCatalog.SystemesScreenScraperJsonPath;
        if (File.Exists(jsonPath))
        {
            try
            {
                var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(jsonPath));
                if (raw != null)
                {
                    foreach (var pair in raw)
                    {
                        var key = NormalizeFrontend(pair.Key);
                        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(pair.Value) && !map.ContainsKey(key))
                        {
                            map[key] = pair.Value.Trim();
                        }
                    }
                }
            }
            catch
            {
                // Fallback legacy below.
            }
        }

        if (map.Count > 0)
        {
            return map;
        }

        var path = _referenceCatalog.SystemesScreenScraperLegacyPath;
        if (!File.Exists(path))
        {
            return map;
        }

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            var parts = trimmed.Split('|', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var key = parts[0].Trim().Replace(' ', '_').ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key))
            {
                map[key] = parts[1].Trim();
            }
        }

        return map;
    }
}
