using RetroBat.Domain.Models;

namespace RetroBat.Domain.Interfaces;

public interface IHiscoreService
{
    Task<HiscoreExtractionResult> ExtractAsync(GameReference targetGame, CancellationToken cancellationToken = default);
}
