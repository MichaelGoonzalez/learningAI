using HandRaise.Domain.Notifications;
using HandRaise.Domain.Rules;

namespace HandRaise.Application.Notifications;

public interface INotificationDispatcher
{
    Task DispatchAsync(OperationalAlert alert, string? analyticTypeId = null, CancellationToken cancellationToken = default);
    Task<NotificationAttempt> TestDestinationAsync(NotificationDestination destination, CancellationToken cancellationToken = default);
}
