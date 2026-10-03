using System.Text.Json.Serialization;

namespace HandRaise.Domain.Notifications;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationDestinationType
{
    Webhook,
    Mqtt,
    Custom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NotificationStatus
{
    Pending,
    Succeeded,
    Failed
}
