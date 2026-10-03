using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HandRaise.Domain.Notifications;

namespace HandRaise.Desktop.ViewModels;

public sealed class DestinationItemViewModel : ObservableObject
{
    public DestinationItemViewModel(NotificationDestination destination)
    {
        Id = destination.Id;
        Name = destination.Name;
        Type = destination.Type;
        Enabled = destination.Enabled;

        if (destination.Configuration != null && destination.Configuration.TryGetValue("url", out var urlVal) && urlVal != null)
        {
            Endpoint = urlVal.ToString() ?? "-";
        }
        else if (destination.Configuration != null && destination.Configuration.TryGetValue("broker", out var brokerVal) && brokerVal != null)
        {
            Endpoint = brokerVal.ToString() ?? "-";
        }
        else
        {
            Endpoint = "-";
        }
    }

    public string Id { get; }
    public string Name { get; }
    public NotificationDestinationType Type { get; }
    public bool Enabled { get; }
    public string Endpoint { get; }
    public string TypeText => Type == NotificationDestinationType.Webhook ? "Webhook HTTP" : "MQTT";
    public string StatusText => Enabled ? "Activo" : "Inactivo";
    public Brush StatusBrush => Enabled
        ? new SolidColorBrush(Color.FromRgb(78, 190, 123))
        : new SolidColorBrush(Color.FromRgb(120, 131, 142));
}

public sealed class PolicyItemViewModel : ObservableObject
{
    public PolicyItemViewModel(NotificationPolicy policy)
    {
        Id = policy.Id;
        Name = policy.Name;
        MinSeverity = policy.SeverityFilter != null && policy.SeverityFilter.Count > 0
            ? string.Join(", ", policy.SeverityFilter)
            : "TODAS";
        Enabled = policy.Enabled;
        DestinationId = policy.DestinationId;
    }

    public string Id { get; }
    public string Name { get; }
    public string MinSeverity { get; }
    public bool Enabled { get; }
    public string DestinationId { get; }
    public string DestinationIdsSummary => string.IsNullOrWhiteSpace(DestinationId) ? "Sin destino" : DestinationId;
    public string StatusText => Enabled ? "Activa" : "Inactiva";
    public Brush StatusBrush => Enabled
        ? new SolidColorBrush(Color.FromRgb(78, 190, 123))
        : new SolidColorBrush(Color.FromRgb(120, 131, 142));
}

public sealed class AttemptItemViewModel : ObservableObject
{
    public AttemptItemViewModel(NotificationAttempt attempt)
    {
        Id = attempt.Id;
        AlertId = attempt.AlertId;
        DestinationId = attempt.DestinationId;
        Success = attempt.Status == NotificationStatus.Succeeded;
        Error = attempt.ErrorSanitized;
        AttemptedAt = attempt.TimestampUtc;
        DurationMs = 0;
    }

    public string Id { get; }
    public string AlertId { get; }
    public string DestinationId { get; }
    public bool Success { get; }
    public string? Error { get; }
    public DateTimeOffset AttemptedAt { get; }
    public double DurationMs { get; }
    public string AttemptedAtFormatted => AttemptedAt.ToLocalTime().ToString("HH:mm:ss");
    public string StatusText => Success ? "Enviado (OK)" : "Error";
    public Brush StatusBrush => Success
        ? new SolidColorBrush(Color.FromRgb(78, 190, 123))
        : new SolidColorBrush(Color.FromRgb(255, 107, 107));
}
