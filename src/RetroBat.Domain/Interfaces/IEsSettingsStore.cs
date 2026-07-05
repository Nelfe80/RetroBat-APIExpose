using System.Xml.Linq;

namespace RetroBat.Domain.Interfaces;

public interface IEsSettingsStore
{
    IReadOnlyDictionary<string, string> ReadAllSettings();

    EmulationStationSettingsSnapshot ReadSnapshot();

    void Invalidate();

    Task WaitForStableFileAsync(CancellationToken cancellationToken = default);

    bool Update(Func<XDocument, bool> update, CancellationToken cancellationToken = default);
}

public sealed record EmulationStationSettingsSnapshot(
    string Path,
    DateTime LastWriteTimeUtc,
    IReadOnlyDictionary<string, string> Values);
