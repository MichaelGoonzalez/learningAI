using HandRaise.Domain.Notifications;

namespace HandRaise.Application.Notifications;

public interface INotificationDestinationStore
{
    Task<IReadOnlyList<NotificationDestination>> ListAllAsync(CancellationToken cancellationToken = default);
    Task<NotificationDestination?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<NotificationDestination> SaveAsync(NotificationDestination destination, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
