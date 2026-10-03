using HandRaise.Domain.Notifications;

namespace HandRaise.Application.Notifications;

public interface INotificationPolicyStore
{
    Task<IReadOnlyList<NotificationPolicy>> ListAllAsync(CancellationToken cancellationToken = default);
    Task<NotificationPolicy?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<NotificationPolicy> SaveAsync(NotificationPolicy policy, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
