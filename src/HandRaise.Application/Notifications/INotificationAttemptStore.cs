using HandRaise.Domain.Notifications;

namespace HandRaise.Application.Notifications;

public sealed record NotificationAttemptQuery(
    string? AlertId = null,
    string? DestinationId = null,
    NotificationStatus? Status = null,
    int Limit = 100,
    int Offset = 0);

public interface INotificationAttemptStore
{
    Task<NotificationAttempt> SaveAsync(NotificationAttempt attempt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<NotificationAttempt>> QueryAsync(NotificationAttemptQuery query, CancellationToken cancellationToken = default);
}
