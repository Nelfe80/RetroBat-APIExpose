using RetroBat.Domain.Models;

namespace RetroBat.Domain.Interfaces;

public interface IHiscoreThemeWriter
{
    Task WriteAsync(GameReference game, HiscoreExtractionResult result, CancellationToken cancellationToken = default);
}
