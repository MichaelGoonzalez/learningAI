using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandRaise.Application.Events;
using HandRaise.Application.Hardware;
using HandRaise.Application.Inference;
using HandRaise.Application.Storage;
using HandRaise.Application.Zones;
using HandRaise.Desktop.Configuration;
using HandRaise.Desktop.Services;
using HandRaise.Host.Services;

namespace HandRaise.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DesktopConfiguration _configuration;
    private readonly NodeHostController _hostController;
    private readonly Dispatcher _dispatcher;
    private readonly Action<bool> _applyTheme;
    private readonly DispatcherTimer _pollTimer;

    private DeviceItemViewModel? _selectedDevice;
    private string? _selectedCameraFilter;
    private DateTime? _historyFrom;
    private DateTime? _historyTo;
    private string _statusMessage = "Listo";
    private bool _isDarkTheme;
    private bool _isNodeBusy;
    private bool _disposed;

    // Node Status
    private string _nodeStatusText = "Iniciando...";
    private Brush _nodeStatusBadgeBrush = new SolidColorBrush(Color.FromRgb(247, 184, 75)); // Yellow
    private string _apiStatusText = "Iniciando...";
    private string _localAddressText = "http://127.0.0.1:5080";
    private string _nodeIdText = "local-node";
    private string _readinessText = "Verificando...";
    private string _processorText = "Detectando hardware...";
    private string _activeCamerasCountText = "0 activas";
    private string _uptimeText = "00:00:00";

    // API & Remote Access Settings
    private bool _isLanAccess;
    private int _apiPort = 5080;
    private string _apiKey = "secret";
    private bool _isApiKeyVisible;

    // Diagnostics
    private NodeDiagnosticsReport? _diagnosticsReport;
    private int _selectedNavigationIndex;

    public MainViewModel(
        DesktopConfiguration configuration,
        NodeHostController hostController,
        Dispatcher dispatcher,
        Action<bool> applyTheme)
    {
        _configuration = configuration;
        _hostController = hostController;
        _dispatcher = dispatcher;
        _applyTheme = applyTheme;

        Devices = [];
        Cameras = [];
        Events = [];
        CameraFilters = ["Todas"];
        SelectedCameraFilter = "Todas";

        StartNodeCommand = new AsyncRelayCommand(StartNodeAsync, () => !_isNodeBusy && !_hostController.IsRunning);
        StopNodeCommand = new AsyncRelayCommand(StopNodeAsync, () => !_isNodeBusy && _hostController.IsRunning);
        RestartNodeCommand = new AsyncRelayCommand(RestartNodeAsync, () => !_isNodeBusy);

        SwitchDeviceCommand = new AsyncRelayCommand(SwitchDeviceAsync);
        RefreshHistoryCommand = new AsyncRelayCommand(RefreshHistoryAsync);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);

        ToggleApiKeyVisibilityCommand = new RelayCommand(() => IsApiKeyVisible = !IsApiKeyVisible);
        GenerateApiKeyCommand = new RelayCommand(GenerateNewApiKey);
        CopyApiKeyCommand = new RelayCommand(CopyApiKeyToClipboard);
        CopyAddressCommand = new RelayCommand(CopyAddressToClipboard);
        ApplyApiSettingsCommand = new AsyncRelayCommand(ApplyApiSettingsAsync);
        RefreshDiagnosticsCommand = new AsyncRelayCommand(RefreshDiagnosticsAsync);

        _isDarkTheme = configuration.Ui.DarkTheme;
        _applyTheme(_isDarkTheme);

        _pollTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, OnPollTimerTick, _dispatcher);
        _hostController.StatusChanged += OnHostStatusChanged;
    }

    public ObservableCollection<CameraViewModel> Cameras { get; }
    public ObservableCollection<DeviceItemViewModel> Devices { get; }
    public ObservableCollection<EventItemViewModel> Events { get; }
    public ObservableCollection<string> CameraFilters { get; }
    public string AboutText { get; } = $"Vision Edge Node · v{Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0"}";

    // Node Status Properties
    public string NodeStatusText { get => _nodeStatusText; private set => SetProperty(ref _nodeStatusText, value); }
    public Brush NodeStatusBadgeBrush { get => _nodeStatusBadgeBrush; private set => SetProperty(ref _nodeStatusBadgeBrush, value); }
    public string ApiStatusText { get => _apiStatusText; private set => SetProperty(ref _apiStatusText, value); }
    public string LocalAddressText { get => _localAddressText; private set => SetProperty(ref _localAddressText, value); }
    public string NodeIdText { get => _nodeIdText; private set => SetProperty(ref _nodeIdText, value); }
    public string ReadinessText { get => _readinessText; private set => SetProperty(ref _readinessText, value); }
    public string ProcessorText { get => _processorText; private set => SetProperty(ref _processorText, value); }
    public string ActiveCamerasCountText { get => _activeCamerasCountText; private set => SetProperty(ref _activeCamerasCountText, value); }
    public string UptimeText { get => _uptimeText; private set => SetProperty(ref _uptimeText, value); }

    public bool IsNodeRunning => _hostController.IsRunning;
    public bool IsNodeBusy { get => _isNodeBusy; private set { if (SetProperty(ref _isNodeBusy, value)) UpdateCommandStates(); } }

    // API & Remote Access Settings
    public bool IsLanAccess { get => _isLanAccess; set => SetProperty(ref _isLanAccess, value); }
    public int ApiPort { get => _apiPort; set => SetProperty(ref _apiPort, value); }
    public string ApiKey { get => _apiKey; set { if (SetProperty(ref _apiKey, value)) OnPropertyChanged(nameof(ApiKeyMasked)); } }
    public string ApiKeyMasked => IsApiKeyVisible ? _apiKey : new string('•', Math.Max(12, _apiKey.Length));
    public bool IsApiKeyVisible { get => _isApiKeyVisible; set { if (SetProperty(ref _isApiKeyVisible, value)) OnPropertyChanged(nameof(ApiKeyMasked)); } }

    public int SelectedNavigationIndex { get => _selectedNavigationIndex; set => SetProperty(ref _selectedNavigationIndex, value); }
    public NodeDiagnosticsReport? DiagnosticsReport { get => _diagnosticsReport; private set => SetProperty(ref _diagnosticsReport, value); }

    public DeviceItemViewModel? SelectedDevice { get => _selectedDevice; set => SetProperty(ref _selectedDevice, value); }
    public string? SelectedCameraFilter { get => _selectedCameraFilter; set => SetProperty(ref _selectedCameraFilter, value); }
    public DateTime? HistoryFrom { get => _historyFrom; set => SetProperty(ref _historyFrom, value); }
    public DateTime? HistoryTo { get => _historyTo; set => SetProperty(ref _historyTo, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool IsDarkTheme { get => _isDarkTheme; private set => SetProperty(ref _isDarkTheme, value); }
    public string ThemeButtonText => IsDarkTheme ? "Tema claro" : "Tema oscuro";

    // Commands
    public IAsyncRelayCommand StartNodeCommand { get; }
    public IAsyncRelayCommand StopNodeCommand { get; }
    public IAsyncRelayCommand RestartNodeCommand { get; }
    public IAsyncRelayCommand SwitchDeviceCommand { get; }
    public IAsyncRelayCommand RefreshHistoryCommand { get; }
    public IRelayCommand ToggleThemeCommand { get; }
    public IRelayCommand ToggleApiKeyVisibilityCommand { get; }
    public IRelayCommand GenerateApiKeyCommand { get; }
    public IRelayCommand CopyApiKeyCommand { get; }
    public IRelayCommand CopyAddressCommand { get; }
    public IAsyncRelayCommand ApplyApiSettingsCommand { get; }
    public IAsyncRelayCommand RefreshDiagnosticsCommand { get; }

    public async Task InitializeAsync()
    {
        IsNodeBusy = true;
        StatusMessage = "Iniciando Vision Edge Node...";
        try
        {
            var bind = IsLanAccess ? "0.0.0.0" : "127.0.0.1";
            var success = await _hostController.StartAsync(new NodeHostSettings(bind, ApiPort, ApiKey));
            if (!success)
            {
                StatusMessage = _hostController.StatusMessage;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al iniciar el nodo: {ex.Message}";
        }
        finally
        {
            IsNodeBusy = false;
            _pollTimer.Start();
            UpdateFromHostState();
        }
    }

    private async Task StartNodeAsync()
    {
        IsNodeBusy = true;
        StatusMessage = "Iniciando nodo...";
        try
        {
            var bind = IsLanAccess ? "0.0.0.0" : "127.0.0.1";
            await _hostController.StartAsync(new NodeHostSettings(bind, ApiPort, ApiKey));
            StatusMessage = "Nodo iniciado correctamente.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"No se pudo iniciar: {ex.Message}";
        }
        finally
        {
            IsNodeBusy = false;
            UpdateFromHostState();
        }
    }

    private async Task StopNodeAsync()
    {
        IsNodeBusy = true;
        StatusMessage = "Deteniendo nodo y liberando cámaras...";
        try
        {
            await _hostController.StopAsync();
            StatusMessage = "Nodo detenido.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al detener: {ex.Message}";
        }
        finally
        {
            IsNodeBusy = false;
            UpdateFromHostState();
        }
    }

    private async Task RestartNodeAsync()
    {
        IsNodeBusy = true;
        StatusMessage = "Reiniciando nodo...";
        try
        {
            var bind = IsLanAccess ? "0.0.0.0" : "127.0.0.1";
            await _hostController.RestartAsync(new NodeHostSettings(bind, ApiPort, ApiKey));
            StatusMessage = "Nodo reiniciado y operativo.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al reiniciar: {ex.Message}";
        }
        finally
        {
            IsNodeBusy = false;
            UpdateFromHostState();
        }
    }

    private async Task ApplyApiSettingsAsync()
    {
        IsNodeBusy = true;
        StatusMessage = "Aplicando configuración de red y reiniciando API...";
        try
        {
            var bind = IsLanAccess ? "0.0.0.0" : "127.0.0.1";
            await _hostController.RestartAsync(new NodeHostSettings(bind, ApiPort, ApiKey));
            StatusMessage = $"API activa en {LocalAddressText}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al aplicar configuración: {ex.Message}";
        }
        finally
        {
            IsNodeBusy = false;
            UpdateFromHostState();
        }
    }

    private void OnPollTimerTick(object? sender, EventArgs e)
    {
        UpdateFromHostState();
    }

    private void UpdateFromHostState()
    {
        LocalAddressText = _hostController.BaseUrl;

        switch (_hostController.Status)
        {
            case NodeHostStatus.Running:
                NodeStatusText = "Operativo";
                NodeStatusBadgeBrush = new SolidColorBrush(Color.FromRgb(78, 190, 123)); // Green
                ApiStatusText = "Disponible";
                break;
            case NodeHostStatus.Starting:
                NodeStatusText = "Iniciando...";
                NodeStatusBadgeBrush = new SolidColorBrush(Color.FromRgb(247, 184, 75)); // Yellow
                ApiStatusText = "No disponible";
                break;
            case NodeHostStatus.PortConflict:
                NodeStatusText = "Puerto 5080 ocupado";
                NodeStatusBadgeBrush = new SolidColorBrush(Color.FromRgb(255, 107, 107)); // Red
                ApiStatusText = "No disponible";
                break;
            case NodeHostStatus.Error:
                NodeStatusText = "Error";
                NodeStatusBadgeBrush = new SolidColorBrush(Color.FromRgb(255, 107, 107)); // Red
                ApiStatusText = "No disponible";
                break;
            default:
                NodeStatusText = "Detenido";
                NodeStatusBadgeBrush = new SolidColorBrush(Color.FromRgb(120, 131, 142)); // Gray
                ApiStatusText = "No disponible";
                break;
        }

        if (_hostController.Metrics is { } metrics)
        {
            var snapshot = metrics.Snapshot();
            NodeIdText = snapshot.NodeId;
            var ts = TimeSpan.FromSeconds(snapshot.UptimeSeconds);
            UptimeText = $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
            var onlineCameras = snapshot.Cameras.Count(c => c.Online);
            ActiveCamerasCountText = $"{onlineCameras} {(onlineCameras == 1 ? "activa" : "activas")}";
        }

        if (_hostController.RuntimeState is { } runtime)
        {
            var active = runtime.Inventory.Devices.FirstOrDefault(d => d.Id == runtime.ActiveDeviceId);
            if (active is not null)
            {
                ProcessorText = $"{active.Name} · {(active.Backend == InferenceBackend.Cpu ? "CPU Fallback" : "DirectML")}";
            }

            if (Devices.Count == 0 && runtime.Inventory.Devices.Count > 0)
            {
                foreach (var device in runtime.Inventory.Devices)
                {
                    Devices.Add(new DeviceItemViewModel(device));
                }
                SelectedDevice = Devices.FirstOrDefault(d => d.Id == runtime.ActiveDeviceId);
            }
        }

        if (_hostController.Preflight is { } preflight && _hostController.Metrics is { } m)
        {
            var report = preflight.BuildReport(m.Snapshot().UptimeSeconds, NodeIdText, "default-site", _hostController.IsRunning);
            ReadinessText = report.Status switch
            {
                NodeHealthStatus.Healthy => "Operativo",
                NodeHealthStatus.Degraded => "Degradado",
                _ => "Fallido"
            };
        }

        UpdateCommandStates();
    }

    private void UpdateCommandStates()
    {
        OnPropertyChanged(nameof(IsNodeRunning));
        StartNodeCommand.NotifyCanExecuteChanged();
        StopNodeCommand.NotifyCanExecuteChanged();
        RestartNodeCommand.NotifyCanExecuteChanged();
    }

    private void OnHostStatusChanged(NodeHostStatus status, string message)
    {
        _dispatcher.BeginInvoke(() =>
        {
            StatusMessage = message;
            UpdateFromHostState();
        });
    }

    private async Task SwitchDeviceAsync()
    {
        if (SelectedDevice is null || _hostController.CameraService is null) return;
        StatusMessage = $"Cambiando a {SelectedDevice.Name}...";
        try
        {
            var result = await _hostController.CameraService.ChangeDeviceAsync(SelectedDevice.Id);
            if (result.Success)
            {
                StatusMessage = $"Dispositivo activo: {SelectedDevice.Name}";
                UpdateFromHostState();
            }
            else
            {
                StatusMessage = $"No fue posible cambiar: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private async Task RefreshHistoryAsync()
    {
        if (_hostController.EventRepository is null) return;
        try
        {
            StatusMessage = "Cargando historial...";
            var query = new EventQuery(
                CameraId: SelectedCameraFilter is null or "Todas" ? null : SelectedCameraFilter,
                From: ToUtc(HistoryFrom, endOfDay: false),
                To: ToUtc(HistoryTo, endOfDay: true),
                Limit: Math.Clamp(_configuration.Ui.HistoryLimit, 1, 1000));
            var history = await _hostController.EventRepository.QueryAsync(query);
            Events.Clear();
            foreach (var handEvent in history)
            {
                Events.Add(EventItemViewModel.Create(handEvent, _configuration.Storage.SnapshotDirectory));
            }
            StatusMessage = $"{history.Count} eventos cargados.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al cargar historial: {ex.Message}";
        }
    }

    private async Task RefreshDiagnosticsAsync()
    {
        if (_hostController.Diagnostics is null) return;
        try
        {
            StatusMessage = "Actualizando diagnóstico...";
            DiagnosticsReport = await Task.Run(() => _hostController.Diagnostics.GetDiagnostics());
            StatusMessage = "Diagnóstico actualizado.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al obtener diagnóstico: {ex.Message}";
        }
    }

    private void GenerateNewApiKey()
    {
        ApiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        StatusMessage = "Nueva API Key generada. Pulse 'Aplicar configuración' para activarla.";
    }

    private void CopyApiKeyToClipboard()
    {
        try
        {
            Clipboard.SetText(ApiKey);
            StatusMessage = "API Key copiada al portapapeles.";
        }
        catch { }
    }

    private void CopyAddressToClipboard()
    {
        try
        {
            Clipboard.SetText(LocalAddressText);
            StatusMessage = "Dirección de API copiada al portapapeles.";
        }
        catch { }
    }

    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        OnPropertyChanged(nameof(ThemeButtonText));
        _applyTheme(IsDarkTheme);
    }

    private static DateTimeOffset? ToUtc(DateTime? value, bool endOfDay)
    {
        if (value is null) return null;
        var local = endOfDay ? value.Value.Date.AddDays(1).AddTicks(-1) : value.Value.Date;
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _hostController.StatusChanged -= OnHostStatusChanged;
        await _hostController.DisposeAsync();
    }
}

