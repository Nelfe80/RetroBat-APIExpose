using System.Text.Json;

namespace RetroBat.Api.Media;

/// <summary>
/// Les demandes de fond que ScreenScraper a deja laissees sans rien de neuf, et jusqu'a quand ne
/// pas les refaire. Gardees sur disque : en memoire seule, chaque redemarrage de l'API (au moins
/// quotidien sur une borne) redemandait tout a ScreenScraper a la premiere visite de chaque jeu,
/// sur un quota souvent partage.
///
/// Non synchronisee : l'appelant tient son verrou.
/// </summary>
internal sealed class RemoteScrapeNoChangeCooldowns
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly Dictionary<string, DateTime> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public RemoteScrapeNoChangeCooldowns(string path)
    {
        _path = path;
    }

    public int Count
    {
        get
        {
            Load();
            return _entries.Count;
        }
    }

    public bool IsActive(string key, DateTime nowUtc)
    {
        Load();
        if (!_entries.TryGetValue(key, out var expiresAtUtc))
        {
            return false;
        }

        if (expiresAtUtc > nowUtc)
        {
            return true;
        }

        _entries.Remove(key);
        Save();
        return false;
    }

    public void Remember(string key, DateTime expiresAtUtc)
    {
        Load();
        _entries[key] = expiresAtUtc;
        Save();
    }

    public void Forget(string key)
    {
        Load();
        if (_entries.Remove(key))
        {
            Save();
        }
    }

    public void Clear()
    {
        _loaded = true;
        _entries.Clear();
        Save();
    }

    private void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var stored = JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(_path), JsonOptions);
            var nowUtc = DateTime.UtcNow;
            foreach (var entry in stored ?? new Dictionary<string, DateTime>())
            {
                // Les echeances perimees ne reviennent pas : le fichier ne grossit pas sans fin.
                if (entry.Value > nowUtc)
                {
                    _entries[entry.Key] = entry.Value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Illisible : on repart a vide, au pire ScreenScraper est redemande une fois.
            _entries.Clear();
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Pas d'ecriture possible : la memoire vive garde le role, comme avant.
        }
    }
}
