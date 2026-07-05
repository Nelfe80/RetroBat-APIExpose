using RetroBat.Domain.Models;

namespace RetroBat.Domain.Interfaces;

public interface IMediaPrefetchService
{
    Task<MediaPrefetchResult> PrefetchForSelectionAsync(GameReference game, CancellationToken cancellationToken = default);
    Task<MediaPrefetchResult> PrefetchForSelectionAsync(GameReference game, bool allowRemoteScrape, CancellationToken cancellationToken = default);
    Task<MediaPrefetchResult> PrefetchForSelectionAsync(GameReference game, bool allowRemoteScrape, bool forceRemoteScrape, bool createUserVariantGuide = false, CancellationToken cancellationToken = default);
    Task<MediaPrefetchResult> PrefetchForSelectionAsync(GameReference game, bool allowRemoteScrape, bool forceRemoteScrape, bool createUserVariantGuide, bool suppressImmediateGamelistUpdates, CancellationToken cancellationToken = default);
    Task<MediaProjectionPlan> PrepareLocalProjectionPlanAsync(GameReference game, CancellationToken cancellationToken = default);
    Task<MediaPrefetchResult> QueueRemoteForSelectionAsync(GameReference game, CancellationToken cancellationToken = default);
    Task<MediaPrefetchResult> ScrapeLivePriorityForSelectionAsync(GameReference game, CancellationToken cancellationToken = default);
}
