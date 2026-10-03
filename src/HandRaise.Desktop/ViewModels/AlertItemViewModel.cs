using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandRaise.Domain.Rules;

namespace HandRaise.Desktop.ViewModels;

public sealed class AlertItemViewModel : ObservableObject
{
    private readonly OperationalAlert _alert;
    private readonly Func<string, Task>? _onAcknowledge;
    private readonly Func<string, Task>? _onResolve;
    private AlertStatus _status;

    public AlertItemViewModel(
        OperationalAlert alert,
        Func<string, Task>? onAcknowledge = null,
        Func<string, Task>? onResolve = null)
    {
        _alert = alert;
        _status = alert.Status;
        _onAcknowledge = onAcknowledge;
        _onResolve = onResolve;

        AcknowledgeCommand = new AsyncRelayCommand(AcknowledgeAsync, () => CanAcknowledge);
        ResolveCommand = new AsyncRelayCommand(ResolveAsync, () => CanResolve);
    }

    public string Id => _alert.Id;
    public string RuleId => _alert.RuleId;
    public string CameraId => _alert.CameraId;
    public string Title => _alert.Title;
    public string Description => _alert.Description;
    public string Message => string.IsNullOrWhiteSpace(_alert.Description) ? _alert.Title : $"{_alert.Title} - {_alert.Description}";
    public AlertSeverity Severity => _alert.Severity;
    public DateTimeOffset CreatedAt => _alert.CreatedAt;
    public string CreatedAtFormatted => _alert.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public AlertStatus Status
    {
        get => _status;
        private set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(CanAcknowledge));
                OnPropertyChanged(nameof(CanResolve));
                AcknowledgeCommand.NotifyCanExecuteChanged();
                ResolveCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusText => _status switch
    {
        AlertStatus.Open => "Abierta",
        AlertStatus.Acknowledged => "Reconocida",
        AlertStatus.Resolved => "Resuelta",
        _ => _status.ToString()
    };

    public string SeverityText => Severity switch
    {
        AlertSeverity.Critical => "CRÍTICA",
        AlertSeverity.High => "ALTA",
        AlertSeverity.Medium => "MEDIA",
        AlertSeverity.Low => "BAJA",
        _ => Severity.ToString()
    };

    public Brush SeverityBadgeBrush => Severity switch
    {
        AlertSeverity.Critical => new SolidColorBrush(Color.FromRgb(255, 75, 75)),
        AlertSeverity.High => new SolidColorBrush(Color.FromRgb(255, 140, 0)),
        AlertSeverity.Medium => new SolidColorBrush(Color.FromRgb(247, 184, 75)),
        _ => new SolidColorBrush(Color.FromRgb(78, 190, 123))
    };

    public bool CanAcknowledge => _status == AlertStatus.Open;
    public bool CanResolve => _status != AlertStatus.Resolved;

    public IAsyncRelayCommand AcknowledgeCommand { get; }
    public IAsyncRelayCommand ResolveCommand { get; }

    private async Task AcknowledgeAsync()
    {
        if (_onAcknowledge != null)
        {
            await _onAcknowledge(Id);
            Status = AlertStatus.Acknowledged;
        }
    }

    private async Task ResolveAsync()
    {
        if (_onResolve != null)
        {
            await _onResolve(Id);
            Status = AlertStatus.Resolved;
        }
    }
}
