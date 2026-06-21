using System.Xml.Linq;
using RetroBat.Domain.Paths;

namespace RetroBat.Api.Infrastructure;

public sealed class EmulationStationSystemConfigService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);
    private readonly object _sync = new();
    private DateTime _lastLoadedAtUtc = DateTime.MinValue;
    private DateTime _lastWriteTimeUtc = DateTime.MinValue;
    private IReadOnlyDictionary<string, IReadOnlyList<EmulationStationSystemEmulatorCore>> _systems =
        new Dictionary<string, IReadOnlyList<EmulationStationSystemEmulatorCore>>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<EmulationStationSystemEmulatorCore> GetEmulatorCores(string systemId)
    {
        var normalizedSystemId = Normalize(systemId);
        if (string.IsNullOrWhiteSpace(normalizedSystemId))
        {
            return Array.Empty<EmulationStationSystemEmulatorCore>();
        }

        var systems = ReadAll();
        return systems.TryGetValue(normalizedSystemId, out var entries)
            ? entries
            : Array.Empty<EmulationStationSystemEmulatorCore>();
    }

    public EmulationStationLaunchConfig ResolveLaunchConfig(
        string systemId,
        IReadOnlyDictionary<string, string> esSettings)
    {
        var normalizedSystemId = Normalize(systemId);
        if (string.IsNullOrWhiteSpace(normalizedSystemId))
        {
            return new EmulationStationLaunchConfig(string.Empty, string.Empty, string.Empty);
        }

        var entries = GetEmulatorCores(normalizedSystemId);
        var configuredEmulator = ReadSetting(esSettings, $"{normalizedSystemId}.emulator");
        var configuredCore = ReadSetting(esSettings, $"{normalizedSystemId}.core");

        var emulator = NormalizeAuto(configuredEmulator);
        var core = NormalizeAuto(configuredCore);

        var selectedEntries = string.IsNullOrWhiteSpace(emulator)
            ? entries
            : entries.Where(entry => string.Equals(entry.Emulator, emulator, StringComparison.OrdinalIgnoreCase)).ToList();
        if (selectedEntries.Count == 0)
        {
            selectedEntries = entries;
        }

        var selected = !string.IsNullOrWhiteSpace(core)
            ? selectedEntries.FirstOrDefault(entry => string.Equals(entry.Core, core, StringComparison.OrdinalIgnoreCase))
            : selectedEntries.FirstOrDefault(entry => !string.IsNullOrWhiteSpace(entry.Core));
        selected ??= selectedEntries.FirstOrDefault();

        return new EmulationStationLaunchConfig(
            normalizedSystemId,
            string.IsNullOrWhiteSpace(emulator) ? selected?.Emulator ?? string.Empty : emulator,
            string.IsNullOrWhiteSpace(core) ? selected?.Core ?? string.Empty : core);
    }

    private IReadOnlyDictionary<string, IReadOnlyList<EmulationStationSystemEmulatorCore>> ReadAll()
    {
        var path = Path.Combine(RetroBatPaths.EmulationStationConfigRoot, "es_systems.cfg");
        var fileExists = File.Exists(path);
        var currentWriteTimeUtc = fileExists ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;

        lock (_sync)
        {
            if (_systems.Count > 0 &&
                currentWriteTimeUtc == _lastWriteTimeUtc &&
                DateTime.UtcNow - _lastLoadedAtUtc < CacheTtl)
            {
                return _systems;
            }

            var systems = new Dictionary<string, IReadOnlyList<EmulationStationSystemEmulatorCore>>(StringComparer.OrdinalIgnoreCase);
            if (fileExists)
            {
                try
                {
                    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
                    foreach (var system in document.Root?.Elements("system") ?? Enumerable.Empty<XElement>())
                    {
                        var systemId = Normalize(system.Element("name")?.Value ?? string.Empty);
                        if (string.IsNullOrWhiteSpace(systemId))
                        {
                            continue;
                        }

                        var entries = new List<EmulationStationSystemEmulatorCore>();
                        foreach (var emulator in system.Element("emulators")?.Elements("emulator") ?? Enumerable.Empty<XElement>())
                        {
                            var emulatorName = Normalize(emulator.Attribute("name")?.Value ?? string.Empty);
                            if (string.IsNullOrWhiteSpace(emulatorName))
                            {
                                continue;
                            }

                            var cores = emulator.Element("cores")?.Elements("core")
                                .Select(core => (core.Value ?? string.Empty).Trim())
                                .Where(core => !string.IsNullOrWhiteSpace(core))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList() ?? new List<string>();

                            if (cores.Count == 0)
                            {
                                entries.Add(new EmulationStationSystemEmulatorCore(systemId, emulatorName, string.Empty));
                                continue;
                            }

                            entries.AddRange(cores.Select(core => new EmulationStationSystemEmulatorCore(systemId, emulatorName, core)));
                        }

                        systems[systemId] = entries;
                    }
                }
                catch
                {
                    if (_systems.Count > 0)
                    {
                        _lastLoadedAtUtc = DateTime.UtcNow;
                        return _systems;
                    }
                }
            }

            _systems = systems;
            _lastWriteTimeUtc = currentWriteTimeUtc;
            _lastLoadedAtUtc = DateTime.UtcNow;
            return _systems;
        }
    }

    private static string ReadSetting(IReadOnlyDictionary<string, string> settings, string key)
    {
        return settings.TryGetValue(key, out var value) ? value.Trim() : string.Empty;
    }

    private static string NormalizeAuto(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.Equals(normalized, "auto", StringComparison.OrdinalIgnoreCase) ? string.Empty : normalized;
    }

    private static string Normalize(string value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }
}

public sealed record EmulationStationSystemEmulatorCore(string SystemId, string Emulator, string Core);

public sealed record EmulationStationLaunchConfig(string SystemId, string Emulator, string Core);
