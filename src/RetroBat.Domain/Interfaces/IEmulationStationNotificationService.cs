namespace RetroBat.Domain.Interfaces;

public interface IEmulationStationNotificationService
{
    Task NotifyAsync(string message, CancellationToken cancellationToken = default);
    Task MessageBoxAsync(string message, CancellationToken cancellationToken = default);
}
