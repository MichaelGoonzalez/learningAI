using HandRaise.Domain.Notifications;

namespace HandRaise.Application.Notifications;

public sealed record NotificationSenderResult(
    bool Success,
    int? StatusCode = null,
    string? Error = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public interface INotificationSender
{
    NotificationDestinationType SupportedType { get; }
    Task<NotificationSenderResult> SendAsync(NotificationDestination destination, WebhookPayload payload, CancellationToken cancellationToken = default);
}
