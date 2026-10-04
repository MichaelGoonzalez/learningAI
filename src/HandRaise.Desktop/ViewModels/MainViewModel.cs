using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandRaise.Application.Capture;
using HandRaise.Application.Events;
using HandRaise.Application.Hardware;
using HandRaise.Application.Inference;
using HandRaise.Application.Settings;
using HandRaise.Application.Storage;
using HandRaise.Application.Zones;
using HandRaise.Desktop.Configuration;
using HandRaise.Desktop.Services;
using HandRaise.Domain.Rules;
using HandRaise.Host.Services;
using HandRaise.Infrastructure.Windows.Capture;
using HandRaise.Infrastructure.Windows.Hardware;

namespace HandRaise.Desktop.ViewModels;

public sealed class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly DesktopConfiguration _configuration;
    private readonly NodeHostController _hostController;
    private readonly INodeCredentialStore _credentialStore;
    private readonly IVideoDeviceEnumerator _videoDeviceEnumerator;
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

    // Navigation (0=Resumen, 1=Cámaras, 2=Alertas, 3=Integraciones, 4=Modelos, 5=Sistema, 6=Conexión Web, 7=Diagnóstico, 8=Detalle Cámara)
    private int _selectedNavIndex = 0;
    private CameraViewModel? _selectedDetailCamera;

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
    private double _aggregateFps = 0.0;
    private string _capacityStateText = "Normal";

    // API & Remote Access Settings
    private bool _isLanAccess;
    private int _apiPort = 5080;
    private string _apiKey = string.Empty;
    private bool _isApiKeyVisible;

    // Add Camera Wizard State
    private bool _isAddCameraModalOpen;
    private int _wizardStep = 1; // 1: Origen, 2: Configuración, 3: Probar, 4: Identidad y Guardar
    private string _wizardSourceType = "USB"; // "USB", "RTSP", "File"
    private string _wizardUsbIndex = "0";
    private VideoDeviceInfo? _selectedUsbDevice;
    private bool _isCustomUsbIndex;
    private string _wizardRtspUrl = "rtsp://";
    private string _wizardRtspUsername = string.Empty;
    private string _wizardRtspPassword = string.Empty;
    private string _wizardFilePath = string.Empty;
    private bool _isTestingConnection;
    private bool? _testConnectionSuccess;
    private string? _testConnectionMessage;
    private string? _testConnectionDetails;
    private BitmapSource? _testPreviewImage;
    private string _newCameraName = "Cámara Nueva";
    private string _newCameraSource = "0";
    private string _newCameraType = "USB";
    private bool _newCameraAutoStart = true;
    private string? _addCameraError;

    // Alerts Filtering
    private string _selectedAlertSeverityFilter = "Todas";
    private string _selectedAlertStatusFilter = "Todas";

    private NodeDiagnosticsReport? _diagnosticsReport;

    public MainViewModel(
        DesktopConfiguration configuration,
        NodeHostController hostController,
        Dispatcher dispatcher,
        Action<bool> applyTheme,
        INodeCredentialStore? credentialStore = null,
        IVideoDeviceEnumerator? videoDeviceEnumerator = null)
    {
        _configuration = configuration;
        _hostController = hostController;
        _dispatcher = dispatcher;
        _applyTheme = applyTheme;
        _videoDeviceEnumerator = videoDeviceEnumerator ?? new WindowsVideoDeviceEnumerator();

        var credPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HandRaiseDetection",
            "node-credentials.json");
        _credentialStore = credentialStore ?? new JsonNodeCredentialStore(credPath, new MachineDpapiProtector());

        Devices = [];
        Cameras = [];
        Events = [];
        Alerts = [];
        Destinations = [];
        Policies = [];
        Attempts = [];
        Models = [];
        CameraFilters = ["Todas"];
        SelectedCameraFilter = "Todas";

        // Navigation Commands
        NavigateCommand = new RelayCommand<object?>(param =>
        {
            if (param is int i)
            {
                SelectedNavIndex = i;
            }
            else if (param is string s && int.TryParse(s, out var parsed))
            {
                SelectedNavIndex = parsed;
            }
        });
        OpenCameraDetailCommand = new AsyncRelayCommand<CameraViewModel>(OpenDetailAsync);
        BackToCamerasCommand = new RelayCommand(() => SelectedNavIndex = 1);
        DeleteCameraCommand = new AsyncRelayCommand<CameraViewModel>(DeleteCameraAsync);

        // Node Controls
        StartNodeCommand = new AsyncRelayCommand(StartNodeAsync, () => !_isNodeBusy && !_hostController.IsRunning);
        StopNodeCommand = new AsyncRelayCommand(StopNodeAsync, () => !_isNodeBusy && _hostController.IsRunning);
        RestartNodeCommand = new AsyncRelayCommand(RestartNodeAsync, () => !_isNodeBusy);

        SwitchDeviceCommand = new AsyncRelayCommand(SwitchDeviceAsync);
        RefreshHistoryCommand = new AsyncRelayCommand(RefreshHistoryAsync);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);

        // Camera Wizard Management
        ShowAddCameraModalCommand = new RelayCommand(OpenAddCameraWizard);
        CloseAddCameraModalCommand = new RelayCommand(() => IsAddCameraModalOpen = false);
        NextWizardStepCommand = new RelayCommand(NextWizardStep);
        PrevWizardStepCommand = new RelayCommand(PrevWizardStep);
        TestWizardConnectionCommand = new AsyncRelayCommand(TestWizardConnectionAsync);
        SaveWizardCameraCommand = new AsyncRelayCommand(SaveWizardCameraAsync);
        AddCameraCommand = new AsyncRelayCommand(AddCameraAsync);
        BrowseVideoFileCommand = new RelayCommand(BrowseVideoFile);

        DetectedUsbDevices = [];

        // Alerts & Integrations Commands
        RefreshAlertsCommand = new AsyncRelayCommand(RefreshAlertsAsync);
        RefreshIntegrationsCommand = new AsyncRelayCommand(RefreshIntegrationsAsync);
        RefreshModelsCommand = new AsyncRelayCommand(RefreshModelsAsync);

        // Web Connection Commands
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

    // Collections
    public ObservableCollection<CameraViewModel> Cameras { get; }
    public ObservableCollection<DeviceItemViewModel> Devices { get; }
    public ObservableCollection<EventItemViewModel> Events { get; }
    public ObservableCollection<AlertItemViewModel> Alerts { get; }
    public ObservableCollection<DestinationItemViewModel> Destinations { get; }
    public ObservableCollection<PolicyItemViewModel> Policies { get; }
    public ObservableCollection<AttemptItemViewModel> Attempts { get; }
    public ObservableCollection<ModelItemViewModel> Models { get; }
    public ObservableCollection<string> CameraFilters { get; }

    public string AppTitle => "VisionControl Edge";
    public string AppSubtitle => "Nodo de Visión Artificial";
    public string AboutText => $"VisionControl Edge · v{Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.9.0-rc.1"}";

    // Navigation Properties
    public int SelectedNavIndex
    {
        get => _selectedNavIndex;
        set
        {
            if (SetProperty(ref _selectedNavIndex, value))
            {
                OnPropertyChanged(nameof(IsOverviewSelected));
                OnPropertyChanged(nameof(IsCamerasSelected));
                OnPropertyChanged(nameof(IsAlertsSelected));
                OnPropertyChanged(nameof(IsIntegrationsSelected));
                OnPropertyChanged(nameof(IsModelsSelected));
                OnPropertyChanged(nameof(IsSystemSelected));
                OnPropertyChanged(nameof(IsWebConnectionSelected));
                OnPropertyChanged(nameof(IsDiagnosticsSelected));
                OnPropertyChanged(nameof(IsCameraDetailSelected));
            }
        }
    }

    public bool IsOverviewSelected
    {
        get => _selectedNavIndex == 0;
        set { if (value) SelectedNavIndex = 0; }
    }
    public bool IsCamerasSelected
    {
        get => _selectedNavIndex == 1;
        set { if (value) SelectedNavIndex = 1; }
    }
    public bool IsAlertsSelected
    {
        get => _selectedNavIndex == 2;
        set { if (value) SelectedNavIndex = 2; }
    }
    public bool IsIntegrationsSelected
    {
        get => _selectedNavIndex == 3;
        set { if (value) SelectedNavIndex = 3; }
    }
    public bool IsModelsSelected
    {
        get => _selectedNavIndex == 4;
        set { if (value) SelectedNavIndex = 4; }
    }
    public bool IsSystemSelected
    {
        get => _selectedNavIndex == 5;
        set { if (value) SelectedNavIndex = 5; }
    }
    public bool IsWebConnectionSelected
    {
        get => _selectedNavIndex == 6;
        set { if (value) SelectedNavIndex = 6; }
    }
    public bool IsDiagnosticsSelected
    {
        get => _selectedNavIndex == 7;
        set { if (value) SelectedNavIndex = 7; }
    }
    public bool IsCameraDetailSelected
    {
        get => _selectedNavIndex == 8;
        set { if (value) SelectedNavIndex = 8; }
    }

    public CameraViewModel? SelectedDetailCamera
    {
        get => _selectedDetailCamera;
        set => SetProperty(ref _selectedDetailCamera, value);
    }

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
    public double AggregateFps { get => _aggregateFps; private set => SetProperty(ref _aggregateFps, value); }
    public string CapacityStateText { get => _capacityStateText; private set => SetProperty(ref _capacityStateText, value); }

    // KPI Counters
    public int TotalCamerasCount => Cameras.Count;
    public int RunningCamerasCount => Cameras.Count(c => c.IsRunning);
    public int TotalActiveAnalyticsCount => Cameras.Sum(c => c.ActiveAnalyticsCount);
    public int OpenAlertsCount => Alerts.Count(a => a.Status == AlertStatus.Open);
    public int CriticalAlertsCount => Alerts.Count(a => a.Severity == AlertSeverity.Critical && a.Status == AlertStatus.Open);

    // Empty State Helpers
    public bool HasCameras => Cameras.Count > 0;
    public bool HasNoCameras => Cameras.Count == 0;
    public bool HasAlerts => Alerts.Count > 0;
    public bool HasNoAlerts => !FilteredAlerts.Any();
    public bool HasDestinations => Destinations.Count > 0;
    public bool HasNoDestinations => Destinations.Count == 0;
    public bool HasPolicies => Policies.Count > 0;
    public bool HasNoPolicies => Policies.Count == 0;
    public bool HasAttempts => Attempts.Count > 0;
    public bool HasNoAttempts => Attempts.Count == 0;
    public bool HasModels => Models.Count > 0;
    public bool HasNoModels => Models.Count == 0;

    public bool IsNodeRunning => _hostController.IsRunning;
    public bool IsNodeBusy { get => _isNodeBusy; private set { if (SetProperty(ref _isNodeBusy, value)) UpdateCommandStates(); } }

    // Add Camera Modal & Wizard Properties
    public bool IsAddCameraModalOpen { get => _isAddCameraModalOpen; set => SetProperty(ref _isAddCameraModalOpen, value); }
    public int WizardStep
    {
        get => _wizardStep;
        set
        {
            if (SetProperty(ref _wizardStep, value))
            {
                OnPropertyChanged(nameof(IsWizardStep1));
                OnPropertyChanged(nameof(IsWizardStep2));
                OnPropertyChanged(nameof(IsWizardStep3));
                OnPropertyChanged(nameof(IsWizardStep4));
            }
        }
    }
    public bool IsWizardStep1 => _wizardStep == 1;
    public bool IsWizardStep2 => _wizardStep == 2;
    public bool IsWizardStep3 => _wizardStep == 3;
    public bool IsWizardStep4 => _wizardStep == 4;

    public string WizardSourceType
    {
        get => _wizardSourceType;
        set
        {
            if (SetProperty(ref _wizardSourceType, value))
            {
                OnPropertyChanged(nameof(IsWizardUsb));
                OnPropertyChanged(nameof(IsWizardRtsp));
                OnPropertyChanged(nameof(IsWizardFile));
                OnPropertyChanged(nameof(EffectiveWizardSource));
            }
        }
    }
    public bool IsWizardUsb
    {
        get => _wizardSourceType == "USB";
        set { if (value) WizardSourceType = "USB"; }
    }
    public bool IsWizardRtsp
    {
        get => _wizardSourceType == "RTSP";
        set { if (value) WizardSourceType = "RTSP"; }
    }
    public bool IsWizardFile
    {
        get => _wizardSourceType == "File";
        set { if (value) WizardSourceType = "File"; }
    }

    public ObservableCollection<VideoDeviceInfo> DetectedUsbDevices { get; }

    public VideoDeviceInfo? SelectedUsbDevice
    {
        get => _selectedUsbDevice;
        set
        {
            if (SetProperty(ref _selectedUsbDevice, value) && value is not null)
            {
                WizardUsbIndex = value.Index.ToString();
                if (string.IsNullOrWhiteSpace(NewCameraName) || NewCameraName == "Cámara Nueva")
                {
                    NewCameraName = value.Name;
                }
            }
        }
    }

    public bool IsCustomUsbIndex
    {
        get => _isCustomUsbIndex;
        set => SetProperty(ref _isCustomUsbIndex, value);
    }

    public string WizardUsbIndex
    {
        get => _wizardUsbIndex;
        set { if (SetProperty(ref _wizardUsbIndex, value)) OnPropertyChanged(nameof(EffectiveWizardSource)); }
    }
    public string WizardRtspUrl
    {
        get => _wizardRtspUrl;
        set { if (SetProperty(ref _wizardRtspUrl, value)) OnPropertyChanged(nameof(EffectiveWizardSource)); }
    }
    public string WizardRtspUsername
    {
        get => _wizardRtspUsername;
        set => SetProperty(ref _wizardRtspUsername, value);
    }
    public string WizardRtspPassword
    {
        get => _wizardRtspPassword;
        set => SetProperty(ref _wizardRtspPassword, value);
    }
    public string WizardFilePath
    {
        get => _wizardFilePath;
        set { if (SetProperty(ref _wizardFilePath, value)) OnPropertyChanged(nameof(EffectiveWizardSource)); }
    }

    public BitmapSource? TestPreviewImage
    {
        get => _testPreviewImage;
        private set
        {
            if (SetProperty(ref _testPreviewImage, value))
            {
                OnPropertyChanged(nameof(HasTestPreviewImage));
            }
        }
    }

    public bool HasTestPreviewImage => _testPreviewImage != null;

    public string EffectiveWizardSource => _wizardSourceType switch
    {
        "USB" => _wizardUsbIndex,
        "RTSP" => _wizardRtspUrl,
        "File" => _wizardFilePath,
        _ => _wizardUsbIndex
    };

    public bool IsTestingConnection { get => _isTestingConnection; private set => SetProperty(ref _isTestingConnection, value); }
    public bool? TestConnectionSuccess
    {
        get => _testConnectionSuccess;
        private set
        {
            if (SetProperty(ref _testConnectionSuccess, value))
            {
                OnPropertyChanged(nameof(HasTestedSuccessfully));
            }
        }
    }
    public bool HasTestedSuccessfully => _testConnectionSuccess == true;
    public string? TestConnectionMessage { get => _testConnectionMessage; private set => SetProperty(ref _testConnectionMessage, value); }
    public string? TestConnectionDetails { get => _testConnectionDetails; private set => SetProperty(ref _testConnectionDetails, value); }

    public bool IsStep2Valid => _wizardSourceType switch
    {
        "USB" => int.TryParse(_wizardUsbIndex?.Trim(), out var idx) && idx >= 0,
        "RTSP" => !string.IsNullOrWhiteSpace(_wizardRtspUrl) &&
                  (_wizardRtspUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
                   _wizardRtspUrl.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase)),
        "File" => !string.IsNullOrWhiteSpace(_wizardFilePath),
        _ => false
    };

    public bool CanSaveCamera => !string.IsNullOrWhiteSpace(NewCameraName) && IsStep2Valid;

    public string NewCameraName { get => _newCameraName; set => SetProperty(ref _newCameraName, value); }
    public string NewCameraSource { get => _newCameraSource; set => SetProperty(ref _newCameraSource, value); }
    public string NewCameraType { get => _newCameraType; set => SetProperty(ref _newCameraType, value); }
    public bool NewCameraAutoStart { get => _newCameraAutoStart; set => SetProperty(ref _newCameraAutoStart, value); }
    public string? AddCameraError { get => _addCameraError; set => SetProperty(ref _addCameraError, value); }

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

    // Alerts Filter Properties
    public string SelectedAlertSeverityFilter
    {
        get => _selectedAlertSeverityFilter;
        set
        {
            if (SetProperty(ref _selectedAlertSeverityFilter, value))
            {
                OnPropertyChanged(nameof(FilteredAlerts));
            }
        }
    }

    public string SelectedAlertStatusFilter
    {
        get => _selectedAlertStatusFilter;
        set
        {
            if (SetProperty(ref _selectedAlertStatusFilter, value))
            {
                OnPropertyChanged(nameof(FilteredAlerts));
            }
        }
    }

    public IEnumerable<AlertItemViewModel> FilteredAlerts
    {
        get
        {
            var result = Alerts.AsEnumerable();
            if (SelectedAlertSeverityFilter != "Todas")
            {
                result = result.Where(a => a.Severity.ToString().Equals(SelectedAlertSeverityFilter, StringComparison.OrdinalIgnoreCase));
            }
            if (SelectedAlertStatusFilter != "Todas")
            {
                result = result.Where(a => a.Status.ToString().Equals(SelectedAlertStatusFilter, StringComparison.OrdinalIgnoreCase)
                    || a.StatusText.Equals(SelectedAlertStatusFilter, StringComparison.OrdinalIgnoreCase));
            }
            return result;
        }
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
    public IRelayCommand<object?> NavigateCommand { get; }
    public IAsyncRelayCommand<CameraViewModel> OpenCameraDetailCommand { get; }
    public IRelayCommand BackToCamerasCommand { get; }
    public IAsyncRelayCommand<CameraViewModel> DeleteCameraCommand { get; }

    public IAsyncRelayCommand StartNodeCommand { get; }
    public IAsyncRelayCommand StopNodeCommand { get; }
    public IAsyncRelayCommand RestartNodeCommand { get; }
    public IAsyncRelayCommand SwitchDeviceCommand { get; }
    public IAsyncRelayCommand RefreshHistoryCommand { get; }
    public IRelayCommand ToggleThemeCommand { get; }

    public IRelayCommand ShowAddCameraModalCommand { get; }
    public IRelayCommand CloseAddCameraModalCommand { get; }
    public IRelayCommand NextWizardStepCommand { get; }
    public IRelayCommand PrevWizardStepCommand { get; }
    public IAsyncRelayCommand TestWizardConnectionCommand { get; }
    public IAsyncRelayCommand SaveWizardCameraCommand { get; }
    public IAsyncRelayCommand AddCameraCommand { get; }
    public IRelayCommand BrowseVideoFileCommand { get; }

    public IAsyncRelayCommand RefreshAlertsCommand { get; }
    public IAsyncRelayCommand RefreshIntegrationsCommand { get; }
    public IAsyncRelayCommand RefreshModelsCommand { get; }

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
        StatusMessage = "Iniciando VisionControl Edge...";
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
            _ = RefreshAlertsAsync();
            _ = RefreshIntegrationsAsync();
            _ = RefreshModelsAsync();
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

    private async Task OpenDetailAsync(CameraViewModel? camera)
    {
        if (camera is null) return;
        SelectedDetailCamera = camera;
        SelectedNavIndex = 8;
        await camera.InitializeZonesAsync();
        await camera.LoadAssignedAnalyticsAsync();
        await camera.RefreshEventsAsync();
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

    private void OpenAddCameraWizard()
    {
        WizardStep = 1;
        WizardSourceType = "USB";
        WizardUsbIndex = "0";
        IsCustomUsbIndex = false;
        WizardRtspUrl = "rtsp://";
        WizardRtspUsername = string.Empty;
        WizardRtspPassword = string.Empty;
        WizardFilePath = string.Empty;
        NewCameraName = "Cámara Nueva";
        NewCameraAutoStart = true;
        AddCameraError = null;
        TestConnectionSuccess = null;
        TestConnectionMessage = null;
        TestConnectionDetails = null;
        TestPreviewImage = null;
        IsAddCameraModalOpen = true;
        _ = PopulateDetectedUsbDevicesAsync();
    }

    public async Task PopulateDetectedUsbDevicesAsync()
    {
        try
        {
            var devices = await _videoDeviceEnumerator.EnumerateDevicesAsync();
            DetectedUsbDevices.Clear();
            foreach (var dev in devices)
            {
                DetectedUsbDevices.Add(dev);
            }
            if (DetectedUsbDevices.Count > 0 && (SelectedUsbDevice is null || !DetectedUsbDevices.Contains(SelectedUsbDevice)))
            {
                SelectedUsbDevice = DetectedUsbDevices[0];
            }
        }
        catch
        {
        }
    }

    private void BrowseVideoFile()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Seleccionar archivo de video",
                Filter = "Archivos de video (*.mp4;*.mkv;*.avi;*.mov)|*.mp4;*.mkv;*.avi;*.mov|Todos los archivos (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                WizardFilePath = dialog.FileName;
                if (string.IsNullOrWhiteSpace(NewCameraName) || NewCameraName == "Cámara Nueva")
                {
                    NewCameraName = Path.GetFileNameWithoutExtension(dialog.FileName);
                }
            }
        }
        catch
        {
        }
    }

    private static BitmapSource DecodeJpeg(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private void NextWizardStep()
    {
        AddCameraError = null;
        if (WizardStep == 2)
        {
            if (!IsStep2Valid)
            {
                AddCameraError = WizardSourceType switch
                {
                    "USB" => "El índice USB debe ser un número entero mayor o igual a 0 (ej. 0, 1).",
                    "RTSP" => "La URL RTSP debe comenzar con 'rtsp://' o 'rtsps://'.",
                    "File" => "Debe ingresar una ruta de archivo de video válida.",
                    _ => "Configuración de origen no válida."
                };
                return;
            }
        }

        if (WizardStep < 4)
        {
            WizardStep++;
            if (WizardStep == 3 && TestConnectionSuccess is null)
            {
                _ = TestWizardConnectionAsync();
            }
        }
    }

    private void PrevWizardStep()
    {
        AddCameraError = null;
        if (WizardStep > 1)
        {
            WizardStep--;
        }
    }

    private async Task TestWizardConnectionAsync()
    {
        if (_hostController.CameraService is null)
        {
            TestConnectionSuccess = false;
            TestConnectionMessage = "Servicio de cámaras no disponible.";
            TestConnectionDetails = "El host no se encuentra en ejecución.";
            TestPreviewImage = null;
            return;
        }

        IsTestingConnection = true;
        TestConnectionSuccess = null;
        TestConnectionMessage = "Probando conexión con la fuente...";
        TestConnectionDetails = null;
        TestPreviewImage = null;

        try
        {
            var req = new CameraTestRequest(
                Source: EffectiveWizardSource,
                Username: string.IsNullOrWhiteSpace(WizardRtspUsername) ? null : WizardRtspUsername,
                Password: string.IsNullOrWhiteSpace(WizardRtspPassword) ? null : WizardRtspPassword);

            var result = await _hostController.CameraService.TestAsync(req);
            if (result.Ok)
            {
                TestConnectionSuccess = true;
                TestConnectionMessage = "¡Cámara lista y verificada!";
                TestConnectionDetails = $"{result.Width}x{result.Height} @ {result.FramesPerSecond:F1} FPS ({result.ConnectionMilliseconds:F0} ms)";
                if (result.PreviewJpeg != null)
                {
                    try
                    {
                        TestPreviewImage = DecodeJpeg(result.PreviewJpeg);
                    }
                    catch
                    {
                    }
                }
            }
            else
            {
                TestConnectionSuccess = false;
                TestConnectionMessage = "Fallo en la prueba de conexión:";
                TestConnectionDetails = result.Error ?? "La cámara no entregó cuadros de video.";
                TestPreviewImage = null;
            }
        }
        catch (Exception ex)
        {
            TestConnectionSuccess = false;
            TestConnectionMessage = "Error inesperado al probar:";
            TestConnectionDetails = ex.Message;
            TestPreviewImage = null;
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    private async Task SaveWizardCameraAsync()
    {
        if (string.IsNullOrWhiteSpace(NewCameraName))
        {
            AddCameraError = "El nombre de la cámara es requerido.";
            return;
        }

        if (!IsStep2Valid)
        {
            AddCameraError = "La configuración de la fuente de video no es válida.";
            return;
        }

        var source = EffectiveWizardSource;
        if (string.IsNullOrWhiteSpace(source))
        {
            AddCameraError = "La fuente de video es requerida.";
            return;
        }

        if (_hostController.CameraStore is null || _hostController.CameraService is null)
        {
            AddCameraError = "El servicio de cámaras no está disponible.";
            return;
        }

        try
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            var req = new CameraWriteRequest(
                Id: id,
                Name: NewCameraName.Trim(),
                Source: source.Trim(),
                Enabled: NewCameraAutoStart,
                Username: string.IsNullOrWhiteSpace(WizardRtspUsername) ? null : WizardRtspUsername,
                Password: string.IsNullOrWhiteSpace(WizardRtspPassword) ? null : WizardRtspPassword);

            await _hostController.CameraService.CreateAsync(req);
            await SyncCamerasAsync();

            IsAddCameraModalOpen = false;
            AddCameraError = null;
            StatusMessage = $"Cámara '{req.Name}' añadida con éxito.";

            // Navigate to camera detail immediately (Camera-first flow)
            var created = Cameras.FirstOrDefault(c => c.Id == id);
            if (created is not null)
            {
                await OpenDetailAsync(created);
            }
        }
        catch (Exception ex)
        {
            AddCameraError = $"Error al añadir cámara: {ex.Message}";
        }
    }

    private async Task AddCameraAsync()
    {
        await SaveWizardCameraAsync();
    }

    private async Task DeleteCameraAsync(CameraViewModel? camera)
    {
        if (camera is null || _hostController.CameraService is null) return;

        var result = MessageBox.Show(
            $"¿Está seguro de que desea eliminar la cámara '{camera.Name}'?",
            "Confirmar eliminación",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            await _hostController.CameraService.DeleteAsync(camera.Id);
            await SyncCamerasAsync();

            if (SelectedDetailCamera?.Id == camera.Id)
            {
                SelectedDetailCamera = null;
                SelectedNavIndex = 1;
            }

            StatusMessage = $"Cámara '{camera.Name}' eliminada.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al eliminar cámara: {ex.Message}";
        }
    }

    public async Task RefreshAlertsAsync()
    {
        if (_hostController.AlertStore is null) return;
        try
        {
            var list = await _hostController.AlertStore.QueryAsync(new HandRaise.Application.Rules.AlertQuery(Limit: 100));
            Alerts.Clear();
            foreach (var a in list)
            {
                Alerts.Add(new AlertItemViewModel(
                    a,
                    onAcknowledge: async id =>
                    {
                        await _hostController.AlertStore.AcknowledgeAsync(id, DateTimeOffset.UtcNow);
                        OnPropertyChanged(nameof(OpenAlertsCount));
                        OnPropertyChanged(nameof(CriticalAlertsCount));
                        OnPropertyChanged(nameof(FilteredAlerts));
                    },
                    onResolve: async id =>
                    {
                        await _hostController.AlertStore.ResolveAsync(id, DateTimeOffset.UtcNow);
                        OnPropertyChanged(nameof(OpenAlertsCount));
                        OnPropertyChanged(nameof(CriticalAlertsCount));
                        OnPropertyChanged(nameof(FilteredAlerts));
                    }));
            }
            OnPropertyChanged(nameof(OpenAlertsCount));
            OnPropertyChanged(nameof(CriticalAlertsCount));
            OnPropertyChanged(nameof(FilteredAlerts));
            OnPropertyChanged(nameof(HasAlerts));
            OnPropertyChanged(nameof(HasNoAlerts));
        }
        catch
        {
        }
    }

    public async Task RefreshIntegrationsAsync()
    {
        try
        {
            if (_hostController.DestinationStore is { } destStore)
            {
                var dests = await destStore.ListAllAsync();
                Destinations.Clear();
                foreach (var d in dests) Destinations.Add(new DestinationItemViewModel(d));
                OnPropertyChanged(nameof(HasDestinations));
                OnPropertyChanged(nameof(HasNoDestinations));
            }

            if (_hostController.PolicyStore is { } polStore)
            {
                var pols = await polStore.ListAllAsync();
                Policies.Clear();
                foreach (var p in pols) Policies.Add(new PolicyItemViewModel(p));
                OnPropertyChanged(nameof(HasPolicies));
                OnPropertyChanged(nameof(HasNoPolicies));
            }

            if (_hostController.AttemptStore is { } attStore)
            {
                var atts = await attStore.QueryAsync(new HandRaise.Application.Notifications.NotificationAttemptQuery(Limit: 50));
                Attempts.Clear();
                foreach (var a in atts) Attempts.Add(new AttemptItemViewModel(a));
                OnPropertyChanged(nameof(HasAttempts));
                OnPropertyChanged(nameof(HasNoAttempts));
            }
        }
        catch
        {
        }
    }

    public async Task RefreshModelsAsync()
    {
        if (_hostController.ModelRegistry is null) return;
        try
        {
            var models = _hostController.ModelRegistry.ListModels();
            Models.Clear();
            foreach (var m in models)
            {
                Models.Add(new ModelItemViewModel(m));
            }
            OnPropertyChanged(nameof(HasModels));
            OnPropertyChanged(nameof(HasNoModels));
        }
        catch
        {
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
                NodeStatusText = "Nodo operativo";
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
            AggregateFps = Math.Round(Cameras.Sum(c => c.FramesPerSecond), 1);
            CapacityStateText = onlineCameras > 0 ? "Normal" : "Inactivo";
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

        OnPropertyChanged(nameof(TotalCamerasCount));
        OnPropertyChanged(nameof(RunningCamerasCount));
        OnPropertyChanged(nameof(TotalActiveAnalyticsCount));
        OnPropertyChanged(nameof(OpenAlertsCount));
        OnPropertyChanged(nameof(CriticalAlertsCount));
        OnPropertyChanged(nameof(HasCameras));
        OnPropertyChanged(nameof(HasNoCameras));

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
            await RefreshAlertsAsync();
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
                    var newVm = new CameraViewModel(
                        view,
                        _hostController.CameraService,
                        _dispatcher,
                        _hostController.AnalyticService,
                        _hostController.AnalyticCatalog,
                        _hostController.LineStore,
                        _hostController.CapabilityPlanner,
                        _hostController.CameraStore,
                        _hostController.EventRepository,
                        _configuration.Storage.SnapshotDirectory);

                    Cameras.Add(newVm);
                    await newVm.InitializeZonesAsync();
                    await newVm.LoadAssignedAnalyticsAsync();
                    if (view.Enabled || view.Running)
                    {
                        await newVm.StartAsync();
                    }
                }
            }
        }
        catch
        {
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
