using RetroBat.Domain.Models;

namespace RetroBat.Domain.Interfaces;

public interface IToastNotificationService
{
    ValueTask EnqueueAsync(ToastNotification notification, CancellationToken cancellationToken = default);
}
