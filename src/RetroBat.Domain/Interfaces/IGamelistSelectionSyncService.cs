namespace RetroBat.Domain.Interfaces;

using RetroBat.Domain.Models;

public interface IGamelistSelectionSyncService
{
    Task<bool> RefreshSelectionsForSystemAsync(string systemId, EmulationStationScrapingSettings? settingsSnapshot = null, CancellationToken cancellationToken = default);
    Task<int> RefreshSelectionsForAllSystemsAsync(EmulationStationScrapingSettings? settingsSnapshot = null, CancellationToken cancellationToken = default);
    Task<int> RefreshSelectionsForAllSystemsAtStartupAsync(EmulationStationScrapingSettings? settingsSnapshot = null, CancellationToken cancellationToken = default);
    Task<int> EnsureDefaultPlaceholdersForAllSystemsAsync(CancellationToken cancellationToken = default);
    Task<int> SyncEsGameIdsForAllSystemsAsync(CancellationToken cancellationToken = default);
}
