using CommunityToolkit.Mvvm.ComponentModel;

namespace HandRaise.Desktop.ViewModels;

public sealed class AnalyticMetricItemViewModel : ObservableObject
{
    public AnalyticMetricItemViewModel() { }

    public AnalyticMetricItemViewModel(
        string instanceId,
        string analyticTypeId,
        string displayName,
        string icon,
        bool enabled,
        string? statusText = null,
        int eventsCount = 0,
        string lastEventText = "Sin actividad reciente")
    {
        InstanceId = instanceId;
        AnalyticTypeId = analyticTypeId;
        DisplayName = displayName;
        Icon = icon;
        Enabled = enabled;
        StatusText = statusText ?? (enabled ? "Activa" : "Pausada");
        EventsCount = eventsCount;
        LastEventText = lastEventText;
    }

    public string InstanceId { get; init; } = string.Empty;
    public string AnalyticTypeId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Icon { get; init; } = "⚡";
    public bool Enabled { get; init; } = true;
    public string StatusText { get; init; } = "Activa";
    public int EventsCount { get; set; }
    public string LastEventText { get; set; } = "Sin actividad reciente";
}
