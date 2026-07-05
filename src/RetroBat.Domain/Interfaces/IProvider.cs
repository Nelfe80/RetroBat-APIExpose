namespace RetroBat.Domain.Interfaces;

public interface IProvider
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    bool IsHealthy();
}
