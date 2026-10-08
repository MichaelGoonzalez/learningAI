using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandRaise.Domain.Analytics;

namespace HandRaise.Desktop.ViewModels;

public sealed class CameraAnalyticItemViewModel : ObservableObject
{
    private readonly CameraAnalyticInstance _instance;
    private readonly AnalyticDefinition? _definition;
    private readonly Func<string, bool, Task>? _onToggle;
    private readonly Func<string, Task>? _onRemove;
    private bool _enabled;

    public CameraAnalyticItemViewModel(
        CameraAnalyticInstance instance,
        AnalyticDefinition? definition = null,
        Func<string, bool, Task>? onToggle = null,
        Func<string, Task>? onRemove = null)
    {
        _instance = instance;
        _definition = definition;
        _enabled = instance.Enabled;
        _onToggle = onToggle;
        _onRemove = onRemove;

        ToggleEnabledCommand = new AsyncRelayCommand(ToggleEnabledAsync);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync);
    }

    public string InstanceId => _instance.Id;
    public string AnalyticTypeId => _instance.AnalyticTypeId;
    public string CameraId => _instance.CameraId;
    public string DisplayName => _definition?.DisplayName ?? _instance.Name ?? _instance.AnalyticTypeId;
    public string Description => _definition?.Description ?? string.Empty;
    public string Category => _definition?.Category switch
    {
        AnalyticCategory.Security => "Seguridad",
        AnalyticCategory.Safety => "Seguridad operativa",
        AnalyticCategory.Operations => "Operaciones",
        AnalyticCategory.Logistics => "Logística",
        AnalyticCategory.General => "General",
        _ => "General"
    };
    public string Version => _definition?.Version ?? "1.0.0";
    public string CapabilitiesSummary => _definition?.RequiredCapabilities.Count > 0
        ? string.Join(", ", _definition.RequiredCapabilities.Select(TranslateCapability))
        : "Detección de posturas y gestos";

    private static string TranslateCapability(InferenceCapability capability) => capability switch
    {
        InferenceCapability.PoseEstimation => "Detección de posturas y gestos",
        InferenceCapability.ObjectDetection => "Detección de personas y objetos",
        InferenceCapability.Tracking => "Seguimiento de movimiento",
        InferenceCapability.Classification => "Clasificación visual",
        InferenceCapability.Segmentation => "Segmentación de áreas",
        _ => capability.ToString()
    };

    public string Icon => AnalyticTypeId switch
    {
        "custom_object_detection" => "",
        "person_presence" => "👤",
        "zone_intrusion" => "🛑",
        "line_crossing" => "↔️",
        "person_counting" => "🔢",
        "hand_raise" => "✋",
        _ => "⚡"
    };

    public string ToggleText => _enabled ? "Pausar" : "Reanudar";

    public string ContextSummary
    {
        get
        {
            var parts = new List<string>();
            if (_instance.AssignedZoneIds is { Count: > 0 } z) parts.Add($"Zona: {string.Join(", ", z)}");
            if (_instance.AssignedLineIds is { Count: > 0 } l) parts.Add($"Línea: {string.Join(", ", l)}");
            if (_instance.Configuration != null)
            {
                if (_instance.Configuration.TryGetValue("confidence_threshold", out var conf) && conf is not null)
                {
                    if (double.TryParse(conf.ToString(), out var cVal)) parts.Add($"Confianza: {cVal:P0}");
                }
                else if (_instance.Configuration.TryGetValue("consecutive_frames", out var cf) && cf is not null)
                {
                    parts.Add($"Cuadros: {cf}");
                }
            }
            return parts.Count > 0 ? string.Join(" · ", parts) : "Configuración estándar";
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(ToggleText));
            }
        }
    }

    public string StatusText => _enabled ? "Activa" : "Pausada";
    public Brush StatusBrush => _enabled
        ? new SolidColorBrush(Color.FromRgb(78, 190, 123))
        : new SolidColorBrush(Color.FromRgb(120, 131, 142));

    public IAsyncRelayCommand ToggleEnabledCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }

    private async Task ToggleEnabledAsync()
    {
        var target = !Enabled;
        if (_onToggle != null)
        {
            await _onToggle(InstanceId, target);
        }
        Enabled = target;
    }

    private async Task RemoveAsync()
    {
        if (_onRemove != null)
        {
            await _onRemove(InstanceId);
        }
    }
}
