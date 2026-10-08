using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandRaise.Domain.Rules;

namespace HandRaise.Desktop.ViewModels;

public sealed class AlertRuleItemViewModel : ObservableObject
{
    private readonly AlertRule _rule;
    private readonly Func<AlertRuleItemViewModel, Task>? _onEdit;
    private readonly Func<AlertRuleItemViewModel, Task>? _onDelete;

    public AlertRuleItemViewModel(
        AlertRule rule,
        Func<AlertRuleItemViewModel, Task>? onEdit = null,
        Func<AlertRuleItemViewModel, Task>? onDelete = null,
        string? analyticDisplayName = null)
    {
        _rule = rule;
        _onEdit = onEdit;
        _onDelete = onDelete;
        if (!string.IsNullOrWhiteSpace(analyticDisplayName))
        {
            AnalyticDisplayName = analyticDisplayName;
        }

        EditCommand = new AsyncRelayCommand(EditAsync);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync);
    }

    public AlertRule Rule => _rule;
    public string Id => _rule.Id;
    public string CameraId => _rule.CameraId;
    public string AnalyticInstanceId => _rule.AnalyticInstanceId;
    public string AnalyticDisplayName { get; set; } = "Solución IA";
    public string Name => _rule.Name;
    public bool Enabled => _rule.Enabled;
    public string StatusText => Enabled ? "Activa" : "Pausada";
    public string EventTypesText
    {
        get
        {
            if (_rule.EventTypes == null || _rule.EventTypes.Count == 0) return "-";
            return string.Join(", ", _rule.EventTypes.Select(TranslateEventType));
        }
    }

    public static string TranslateEventType(string eventType) => eventType switch
    {
        "custom_object_detected" => "Objeto personalizado detectado",
        "hand_raised" => "Mano levantada",
        "hand_lowered" => "Mano bajada",
        "person_presence_started" => "Persona detectada",
        "person_presence_ended" => "Persona retirada",
        "zone_intrusion_started" => "Intrusión en zona",
        "zone_intrusion_ended" => "Fin de intrusión",
        "line_crossed" => "Cruce de línea",
        "person_count_updated" => "Conteo de personas",
        "occupancy_threshold_reached" => "Aforo superado",
        _ => eventType.Replace('_', ' ')
    };

    public AlertSeverity Severity => _rule.Severity;

    public string SeverityText => _rule.Severity switch
    {
        AlertSeverity.Low => "Baja",
        AlertSeverity.Medium => "Media",
        AlertSeverity.High => "Alta",
        AlertSeverity.Critical => "Crítica",
        _ => "Media"
    };

    public Brush SeverityBrush => _rule.Severity switch
    {
        AlertSeverity.Low => new SolidColorBrush(Color.FromRgb(59, 130, 246)),
        AlertSeverity.Medium => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
        AlertSeverity.High => new SolidColorBrush(Color.FromRgb(249, 115, 22)),
        AlertSeverity.Critical => new SolidColorBrush(Color.FromRgb(239, 68, 68)),
        _ => new SolidColorBrush(Color.FromRgb(245, 158, 11))
    };

    public long CooldownMs => _rule.CooldownMs;
    public int CooldownSeconds => (int)Math.Max(0, _rule.CooldownMs / 1000);
    public string CooldownText => $"{CooldownSeconds} seg";

    public IAsyncRelayCommand EditCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }

    private Task EditAsync() => _onEdit != null ? _onEdit(this) : Task.CompletedTask;
    private Task DeleteAsync() => _onDelete != null ? _onDelete(this) : Task.CompletedTask;
}
