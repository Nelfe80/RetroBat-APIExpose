using System.Xml.Linq;
using RetroBat.Domain.Interfaces;
using RetroBat.Domain.Paths;

namespace RetroBat.Domain.Services;

public sealed class EsSettingsStore : IEsSettingsStore
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);
    private readonly object _sync = new();
    private DateTime _lastLoadedAtUtc = DateTime.MinValue;
    private DateTime _lastWriteTimeUtc = DateTime.MinValue;
    private IReadOnlyDictionary<string, string> _cachedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> ReadAllSettings()
    {
        return ReadSnapshot().Values;
    }

    public EmulationStationSettingsSnapshot ReadSnapshot()
    {
        var path = RetroBatPaths.EmulationStationSettingsPath;
        var fileExists = File.Exists(path);
        var currentWriteTimeUtc = fileExists ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

        lock (_sync)
        {
            if (_cachedValues.Count > 0 &&
                currentWriteTimeUtc == _lastWriteTimeUtc &&
                DateTime.UtcNow - _lastLoadedAtUtc < CacheTtl)
            {
                return new EmulationStationSettingsSnapshot(path, _lastWriteTimeUtc, _cachedValues);
            }

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (fileExists)
            {
                try
                {
                    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
                    foreach (var element in document.Descendants().Where(node => node.Attribute("name") != null))
                    {
                        var name = element.Attribute("name")?.Value;
                        var value = element.Attribute("value")?.Value ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(name) && !values.ContainsKey(name))
                        {
                            values[name] = value;
                        }
                    }
                }
                catch
                {
                    if (_cachedValues.Count > 0)
                    {
                        _lastLoadedAtUtc = DateTime.UtcNow;
                        return new EmulationStationSettingsSnapshot(path, _lastWriteTimeUtc, _cachedValues);
                    }

                    values.Clear();
                }
            }

            _cachedValues = values;
            _lastWriteTimeUtc = currentWriteTimeUtc;
            _lastLoadedAtUtc = DateTime.UtcNow;
            return new EmulationStationSettingsSnapshot(path, _lastWriteTimeUtc, _cachedValues);
        }
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _cachedValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _lastWriteTimeUtc = DateTime.MinValue;
            _lastLoadedAtUtc = DateTime.MinValue;
        }
    }

    public async Task WaitForStableFileAsync(CancellationToken cancellationToken = default)
    {
        var path = RetroBatPaths.EmulationStationSettingsPath;
        var lastWriteTimeUtc = DateTime.MinValue;
        var lastLength = -1L;
        var stableSamples = 0;

        for (var attempt = 0; attempt < 16; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = File.Exists(path) ? new FileInfo(path) : null;
            var currentWriteTimeUtc = info?.LastWriteTimeUtc ?? DateTime.MinValue;
            var currentLength = info?.Length ?? -1L;

            if (currentWriteTimeUtc == lastWriteTimeUtc && currentLength == lastLength)
            {
                stableSamples++;
                if (stableSamples >= 2)
                {
                    return;
                }
            }
            else
            {
                stableSamples = 0;
                lastWriteTimeUtc = currentWriteTimeUtc;
                lastLength = currentLength;
            }

            await Task.Delay(150, cancellationToken);
        }
    }

    public bool Update(Func<XDocument, bool> update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var path = RetroBatPaths.EmulationStationSettingsPath;
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        Directory.CreateDirectory(directory);

        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = File.Exists(path)
                ? LoadExistingDocument(path)
                : new XDocument(new XDeclaration("1.0", null, null), new XElement("config"));
            if (!update(document))
            {
                return false;
            }

            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            document.Save(tempPath);
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }

            Invalidate();
            return true;
        }
    }

    private static XDocument LoadExistingDocument(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }
}
