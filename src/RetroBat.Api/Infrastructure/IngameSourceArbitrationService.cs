using RetroBat.Domain.Interfaces;

namespace RetroBat.Api.Infrastructure;

public sealed class IngameSourceArbitrationService : IIngameSourceArbitrationService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, MameLuaSession> _mameLuaSessions = new(StringComparer.OrdinalIgnoreCase);

    public void MarkMameLuaSessionStarted(string systemId, string rom, string definitionFile)
    {
        var session = new MameLuaSession(
            NormalizeSystem(systemId),
            NormalizeRom(rom),
            NormalizePath(definitionFile));

        lock (_lock)
        {
            _mameLuaSessions[BuildKey(session.SystemId, session.Rom, session.DefinitionFile)] = session;
        }
    }

    public void MarkMameLuaSessionStopped(string systemId, string rom, string definitionFile)
    {
        var normalizedSystem = NormalizeSystem(systemId);
        var normalizedRom = NormalizeRom(rom);
        var normalizedDefinition = NormalizePath(definitionFile);

        lock (_lock)
        {
            var keys = _mameLuaSessions
                .Where(entry =>
                    Matches(entry.Value.SystemId, normalizedSystem) ||
                    Matches(entry.Value.Rom, normalizedRom) ||
                    Matches(entry.Value.DefinitionFile, normalizedDefinition))
                .Select(entry => entry.Key)
                .ToList();

            foreach (var key in keys)
            {
                _mameLuaSessions.Remove(key);
            }
        }
    }

    public bool ShouldSuppressRetroArchWrapper(string systemId, string rom, string definitionFile)
    {
        var normalizedSystem = NormalizeSystem(systemId);
        var normalizedRom = NormalizeRom(rom);
        var normalizedDefinition = NormalizePath(definitionFile);

        lock (_lock)
        {
            if (_mameLuaSessions.Count == 0)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(normalizedSystem) &&
                string.IsNullOrWhiteSpace(normalizedRom) &&
                string.IsNullOrWhiteSpace(normalizedDefinition))
            {
                return true;
            }

            if (IsMameOrArcade(normalizedSystem))
            {
                return true;
            }

            return _mameLuaSessions.Values.Any(session =>
                Matches(session.Rom, normalizedRom) ||
                Matches(session.DefinitionFile, normalizedDefinition));
        }
    }

    private static string BuildKey(string systemId, string rom, string definitionFile)
        => $"{systemId}|{rom}|{definitionFile}";

    private static bool Matches(string left, string right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool IsMameOrArcade(string systemId)
        => systemId is "" or "arcade" or "mame";

    private static string NormalizeSystem(string systemId)
        => (systemId ?? string.Empty).Trim().ToLowerInvariant();

    private static string NormalizeRom(string rom)
    {
        var normalized = Path.GetFileNameWithoutExtension((rom ?? string.Empty).Trim());
        return normalized.ToLowerInvariant();
    }

    private static string NormalizePath(string path)
        => string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFullPath(path).Trim().ToLowerInvariant();

    private sealed record MameLuaSession(string SystemId, string Rom, string DefinitionFile);
}
