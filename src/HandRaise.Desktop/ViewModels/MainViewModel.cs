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
using HandRaise.Application.Settings;
using HandRaise.Application.Storage;
using HandRaise.Application.Zones;
using HandRaise.Desktop.Configuration;
using HandRaise.Desktop.Services;
using HandRaise.Host.Services;
using HandRaise.Infrastructure.Windows.Hardware;

namespace HandRaise.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DesktopConfiguration _configuration;
    private readonly NodeHostController _hostController;
    private readonly INodeCredentialStore _credentialStore;
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
    private string _lanAddressText = "Detectando red...";
    private string? _lanIp;
    private string _nodeIdText = "local-node";
    private string _siteIdText = "default-site";
    private string _readinessText = "Verificando...";
    private string _processorText = "Detectando hardware...";
    private string _activeCamerasCountText = "0 activas";
    private string _uptimeText = "00:00:00";

    // API & Remote Access Settings
    private bool _isLanAccess;
    private int _apiPort = 5080;
    private string _apiKey = string.Empty;
    private bool _isApiKeyVisible;

    // Navigation (Right panel tabs: 0 = Conexión Web, 1 = Eventos, 2 = Diagnóstico)
    private int _selectedRightTab;
    private NodeDiagnosticsReport? _diagnosticsReport;

    public MainViewModel(
        DesktopConfiguration configuration,
        NodeHostController hostController,
        Dispatcher dispatcher,
        Action<bool> applyTheme,
        INodeCredentialStore? credentialStore = null)
    {
        _configuration = configuration;
        _hostController = hostController;
        _dispatcher = dispatcher;
        _applyTheme = applyTheme;

        var credPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HandRaiseDetection",
            "node-credentials.json");
        _credentialStore = credentialStore ?? new JsonNodeCredentialStore(credPath, new MachineDpapiProtector());

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

        ToggleApiKeyVisibilityCommand = new RelayCommand(ToggleApiKeyVisibility);
        CopyApiKeyCommand = new RelayCommand(CopyApiKeyToClipboard);
        CopyAddressCommand = new RelayCommand(CopyLocalAddressToClipboard);
        CopyLanAddressCommand = new RelayCommand(CopyLanAddressToClipboard);
        CopyConfigurationCommand = new RelayCommand(CopyConfigurationToClipboard);
        RegenerateApiKeyCommand = new AsyncRelayCommand(RegenerateApiKeyAsync);
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
    public string LanAddressText { get => _lanAddressText; private set => SetProperty(ref _lanAddressText, value); }
    public bool HasLanAddress => !string.IsNullOrWhiteSpace(_lanIp);
    public string ActiveConnectionUrl => IsLanAccess && HasLanAddress ? LanAddressText : LocalAddressText;

    public string NodeIdText { get => _nodeIdText; private set => SetProperty(ref _nodeIdText, value); }
    public string SiteIdText { get => _siteIdText; private set => SetProperty(ref _siteIdText, value); }
    public string ReadinessText { get => _readinessText; private set => SetProperty(ref _readinessText, value); }
    public string ProcessorText { get => _processorText; private set => SetProperty(ref _processorText, value); }
    public string ActiveCamerasCountText { get => _activeCamerasCountText; private set => SetProperty(ref _activeCamerasCountText, value); }
    public string UptimeText { get => _uptimeText; private set => SetProperty(ref _uptimeText, value); }

    public bool IsNodeRunning => _hostController.IsRunning;
    public bool IsNodeBusy { get => _isNodeBusy; private set { if (SetProperty(ref _isNodeBusy, value)) UpdateCommandStates(); } }

    // API & Remote Access Settings
    public bool IsLanAccess
    {
        get => _isLanAccess;
        set
        {
            if (SetProperty(ref _isLanAccess, value))
            {
                OnPropertyChanged(nameof(IsLocalOnlyAccess));
                OnPropertyChanged(nameof(ActiveConnectionUrl));
                if (_hostController.IsRunning)
                {
                    _ = ApplyApiSettingsAsync();
                }
            }
        }
    }

    public bool IsLocalOnlyAccess
    {
        get => !_isLanAccess;
        set
        {
            if (value)
            {
                IsLanAccess = false;
            }
        }
    }

    public int ApiPort { get => _apiPort; set => SetProperty(ref _apiPort, value); }
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (SetProperty(ref _apiKey, value))
            {
                OnPropertyChanged(nameof(ApiKeyMasked));
            }
        }
    }

    public string ApiKeyMasked => IsApiKeyVisible
        ? _apiKey
        : new string('•', Math.Max(16, string.IsNullOrEmpty(_apiKey) ? 16 : _apiKey.Length));

    public bool IsApiKeyVisible
    {
        get => _isApiKeyVisible;
        set
        {
            if (SetProperty(ref _isApiKeyVisible, value))
            {
                OnPropertyChanged(nameof(ApiKeyMasked));
                OnPropertyChanged(nameof(ApiKeyVisibilityButtonText));
            }
        }
    }

    public string ApiKeyVisibilityButtonText => IsApiKeyVisible ? "Ocultar" : "Mostrar";

    // Tab Navigation
    public int SelectedRightTab
    {
        get => _selectedRightTab;
        set
        {
            if (SetProperty(ref _selectedRightTab, value))
            {
                OnPropertyChanged(nameof(IsWebConnectionTabSelected));
                OnPropertyChanged(nameof(IsEventsTabSelected));
                OnPropertyChanged(nameof(IsDiagnosticsTabSelected));
            }
        }
    }

    public bool IsWebConnectionTabSelected
    {
        get => _selectedRightTab == 0;
        set { if (value) SelectedRightTab = 0; }
    }

    public bool IsEventsTabSelected
    {
        get => _selectedRightTab == 1;
        set { if (value) SelectedRightTab = 1; }
    }

    public bool IsDiagnosticsTabSelected
    {
        get => _selectedRightTab == 2;
        set { if (value) SelectedRightTab = 2; }
    }

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
    public IRelayCommand CopyApiKeyCommand { get; }
    public IRelayCommand CopyAddressCommand { get; }
    public IRelayCommand CopyLanAddressCommand { get; }
    public IRelayCommand CopyConfigurationCommand { get; }
    public IAsyncRelayCommand RegenerateApiKeyCommand { get; }
    public IAsyncRelayCommand ApplyApiSettingsCommand { get; }
    public IAsyncRelayCommand RefreshDiagnosticsCommand { get; }

    public async Task InitializeAsync()
    {
        IsNodeBusy = true;
        StatusMessage = "Iniciando Vision Edge Node...";
        try
        {
            var key = await _credentialStore.GetOrCreateApiKeyAsync();
            ApiKey = key;

            _lanIp = NetworkAddressHelper.GetPreferredLanIpv4Address();
            UpdateAddresses();

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
            _ = SyncCamerasAsync();
        }
    }

    private void UpdateAddresses()
    {
        LocalAddressText = $"http://127.0.0.1:{ApiPort}";
        LanAddressText = !string.IsNullOrWhiteSpace(_lanIp)
            ? $"http://{_lanIp}:{ApiPort}"
            : "No disponible (sin red LAN detectada)";
        OnPropertyChanged(nameof(HasLanAddress));
        OnPropertyChanged(nameof(ActiveConnectionUrl));
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
            _ = SyncCamerasAsync();
        }
    }

    private async Task StopNodeAsync()
    {
        IsNodeBusy = true;
        StatusMessage = "Deteniendo nodo y liberando cámaras...";
        try
        {
            for (var i = 0; i < Cameras.Count; i++)
            {
                await Cameras[i].StopStreamingAsync();
            }
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
        StatusMessage = "Aplicando configuración de red...";
        try
        {
            UpdateAddresses();
            var bind = IsLanAccess ? "0.0.0.0" : "127.0.0.1";
            await _hostController.RestartAsync(new NodeHostSettings(bind, ApiPort, ApiKey));
            StatusMessage = $"API activa en {ActiveConnectionUrl}";
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
        _ = SyncCamerasAsync();
    }

    private void UpdateFromHostState()
    {
        UpdateAddresses();

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
                ApiStatusText = "Iniciando...";
                break;
            case NodeHostStatus.PortConflict:
                NodeStatusText = $"Puerto {_apiPort} ocupado";
                NodeStatusBadgeBrush = new SolidColorBrush(Color.FromRgb(255, 107, 107)); // Red
                ApiStatusText = "Puerto ocupado";
                break;
            case NodeHostStatus.Error:
                NodeStatusText = "Error";
                NodeStatusBadgeBrush = new SolidColorBrush(Color.FromRgb(255, 107, 107)); // Red
                ApiStatusText = "Error";
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
            SiteIdText = snapshot.SiteId;
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
            var report = preflight.BuildReport(m.Snapshot().UptimeSeconds, NodeIdText, SiteIdText, _hostController.IsRunning);
            ReadinessText = Enum.TryParse<NodeHealthStatus>(
                report.Status,
                ignoreCase: true,
                out var status)
                ? status switch
                {
                    NodeHealthStatus.Healthy => "Operativo",
                    NodeHealthStatus.Degraded => "Degradado",
                    _ => "Fallido"
                }
                : "Fallido";
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
        _dispatcher.BeginInvoke(async () =>
        {
            StatusMessage = message;
            UpdateFromHostState();
            await SyncCamerasAsync();
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

    private void ToggleApiKeyVisibility()
    {
        IsApiKeyVisible = !IsApiKeyVisible;
    }

    private void CopyApiKeyToClipboard()
    {
        try
        {
            Clipboard.SetText(ApiKey);
            StatusMessage = "Clave de acceso copiada al portapapeles.";
        }
        catch { }
    }

    private void CopyLocalAddressToClipboard()
    {
        try
        {
            Clipboard.SetText(LocalAddressText);
            StatusMessage = "Dirección local copiada al portapapeles.";
        }
        catch { }
    }

    private void CopyLanAddressToClipboard()
    {
        if (!HasLanAddress) return;
        try
        {
            Clipboard.SetText(LanAddressText);
            StatusMessage = "Dirección LAN copiada al portapapeles.";
        }
        catch { }
    }

    private void CopyConfigurationToClipboard()
    {
        try
        {
            var config = new NodeConnectionConfig(ActiveConnectionUrl, ApiKey);
            Clipboard.SetText(config.ToJson());
            StatusMessage = "Configuración JSON copiada al portapapeles (lista para conectar en React).";
        }
        catch { }
    }

    private async Task RegenerateApiKeyAsync()
    {
        var confirm = MessageBox.Show(
            "¿Está seguro de que desea regenerar la clave de acceso?\n\nLas conexiones existentes con el módulo web dejarán de funcionar hasta que actualice la nueva clave en React.",
            "Confirmar regeneración de clave",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var newKey = await _credentialStore.RegenerateApiKeyAsync();
            ApiKey = newKey;
            _hostController.UpdateApiKey(newKey);
            StatusMessage = "Nueva clave de acceso generada y aplicada correctamente.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al regenerar clave: {ex.Message}";
        }
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

    private bool _isSyncingCameras;
    private async Task SyncCamerasAsync()
    {
        if (_isSyncingCameras || _hostController.CameraService is null || !_hostController.IsRunning)
        {
            return;
        }

        _isSyncingCameras = true;
        try
        {
            var cameraViews = await _hostController.CameraService.ListAsync();

            foreach (var view in cameraViews)
            {
                if (!CameraFilters.Contains(view.Id))
                {
                    CameraFilters.Add(view.Id);
                }
            }

            var currentIds = cameraViews.Select(v => v.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var i = Cameras.Count - 1; i >= 0; i--)
            {
                if (!currentIds.Contains(Cameras[i].Id))
                {
                    var vm = Cameras[i];
                    Cameras.RemoveAt(i);
                    await vm.DisposeAsync();
                }
            }

            foreach (var view in cameraViews)
            {
                var existing = Cameras.FirstOrDefault(c => string.Equals(c.Id, view.Id, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.UpdateFromView(view);
                }
                else
                {
                    var newVm = new CameraViewModel(view, _hostController.CameraService, _dispatcher);
                    Cameras.Add(newVm);
                    await newVm.InitializeZonesAsync();
                    if (view.Enabled || view.Running)
                    {
                        await newVm.StartAsync();
                    }
                }
            }
        }
        catch
        {
            // Error transitorio durante transición de nodo
        }
        finally
        {
            _isSyncingCameras = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _hostController.StatusChanged -= OnHostStatusChanged;
        for (var i = 0; i < Cameras.Count; i++)
        {
            await Cameras[i].DisposeAsync();
        }
        Cameras.Clear();
        await _hostController.DisposeAsync();
    }
}
