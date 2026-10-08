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
    public TrainingLabViewModel TrainingLab { get; }
    public bool IsTrainingSelected => _selectedNavIndex == 10;

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
    private string _nodeStatusText = "Sistema listo";
    private Brush _nodeStatusBadgeBrush = new SolidColorBrush(Color.FromRgb(78, 190, 123)); // Green

    // First Run Experience State
    private int _firstRunStep = 1; // 1: Bienvenida, 2: Origen, 3: Configurar, 4: Probar, 5: Nombrar y Guardar, 6: Cámara lista
    private bool _isFirstRunCompleted;
    private CameraViewModel? _savedFirstRunCamera;
    private int _settingsTabIndex = 0;
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
    private string? _testTechnicalDetails;
    private bool _allowSaveWithoutProbe;
    private bool _loopVideoFile = true;
    private BitmapSource? _testPreviewImage;
    private bool _isProbeStreaming;
    private int _probeSessionId;
    private double _probeFps;
    private string _probeFpsText = "0 FPS";
    private string _probeResolutionText = "-";
    private string _probeLatencyText = "-";
    private IAsyncDisposable? _probeSubscription;
    private CancellationTokenSource? _probeCancellation;
    private Task? _probePumpTask;
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
        TrainingLab = new TrainingLabViewModel(_hostController, () => SelectedNavIndex = 1);
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
        NavigateToAnalyticsCommand = new RelayCommand(() =>
        {
            if (Cameras.Count > 0)
            {
                SelectedDetailCamera ??= Cameras[0];
                SelectedDetailCamera.IsAnalyticsTabSelected = true;
                SelectedNavIndex = 8;
            }
            else
            {
                SelectedNavIndex = 1;
                StatusMessage = "Agregue una cámara primero para configurar Soluciones IA.";
            }
        });
        NavigateToRulesCommand = new RelayCommand(() =>
        {
            if (Cameras.Count > 0)
            {
                SelectedDetailCamera ??= Cameras[0];
                SelectedDetailCamera.IsRulesTabSelected = true;
                SelectedNavIndex = 8;
            }
            else
            {
                SelectedNavIndex = 1;
                StatusMessage = "Agregue una cámara primero para configurar Reglas.";
            }
        });
        NavigateToEventsCommand = new RelayCommand(() =>
        {
            if (Cameras.Count > 0)
            {
                SelectedDetailCamera ??= Cameras[0];
                SelectedDetailCamera.IsEventsTabSelected = true;
                SelectedNavIndex = 8;
            }
            else
            {
                SelectedNavIndex = 1;
                StatusMessage = "Agregue una cámara primero para consultar Eventos.";
            }
        });
        DismissPostSaveBannerCommand = new RelayCommand(() => ShowPostSaveBanner = false);
        ConfigureAiAfterSaveCommand = new RelayCommand(() =>
        {
            ShowPostSaveBanner = false;
            if (SelectedDetailCamera is not null)
            {
                SelectedDetailCamera.IsAnalyticsTabSelected = true;
            }
        });
        OpenCameraDetailCommand = new AsyncRelayCommand<CameraViewModel>(OpenDetailAsync);
        OpenCameraByIdCommand = new AsyncRelayCommand<string>(OpenCameraByIdAsync);
        BackToCamerasCommand = new AsyncRelayCommand(async () =>
        {
            if (SelectedDetailCamera != null)
            {
                await SelectedDetailCamera.StopStreamingAsync();
            }
            SelectedNavIndex = 1;
        });
        DuplicateCameraCommand = new AsyncRelayCommand<CameraViewModel>(DuplicateCameraAsync);
        OpenCameraMetricsCommand = new AsyncRelayCommand<CameraViewModel>(OpenCameraMetricsAsync);
        BackToMetricsOverviewCommand = new AsyncRelayCommand(BackToMetricsOverviewAsync);
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
        CloseAddCameraModalCommand = new RelayCommand(() => { IsAddCameraModalOpen = false; _ = StopProbePreviewAsync(); });
        NextWizardStepCommand = new RelayCommand(NextWizardStep);
        PrevWizardStepCommand = new RelayCommand(PrevWizardStep);
        TestWizardConnectionCommand = new AsyncRelayCommand(TestWizardConnectionAsync);
        SaveWizardCameraCommand = new AsyncRelayCommand(SaveWizardCameraAsync);
        AddCameraCommand = new AsyncRelayCommand(AddCameraAsync);
        BrowseVideoFileCommand = new RelayCommand(BrowseVideoFile);
        RefreshUsbDevicesCommand = new AsyncRelayCommand(PopulateDetectedUsbDevicesAsync);

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

        // First Run Experience Commands
        FirstRunStartCommand = new RelayCommand(() =>
        {
            IsProbeStreaming = false;
            IsTestingConnection = false;
            _ = StopProbePreviewAsync();
            FirstRunStep = 2;
        });
        FirstRunSelectSourceCommand = new RelayCommand<string>(src =>
        {
            IsProbeStreaming = false;
            IsTestingConnection = false;
            _ = StopProbePreviewAsync();
            if (!string.IsNullOrEmpty(src)) WizardSourceType = src;
        });
        FirstRunNextCommand = new RelayCommand(FirstRunNext);
        FirstRunBackCommand = new RelayCommand(FirstRunBack);
        FirstRunSetCameraNameSuggestionCommand = new RelayCommand<string>(sug =>
        {
            if (!string.IsNullOrWhiteSpace(sug)) NewCameraName = sug;
        });
        FirstRunSaveCameraCommand = new AsyncRelayCommand(FirstRunSaveCameraAsync);
        FirstRunConfigureAiCommand = new RelayCommand(FirstRunConfigureAi);
        FirstRunGoToAppCommand = new RelayCommand(FirstRunGoToApp);

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
    public string AppSubtitle => "Sistema de Visión Artificial";
    public string AboutText => "VisionControl Edge · v0.9.0";
    public string FullBuildVersionText => $"VisionControl Edge · v{Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.9.0-rc.1"}";

    // Navigation Properties
    private CameraViewModel? _selectedMetricsCamera;
    public CameraViewModel? SelectedMetricsCamera
    {
        get => _selectedMetricsCamera;
        set
        {
            if (SetProperty(ref _selectedMetricsCamera, value))
            {
                OnPropertyChanged(nameof(IsMetricsDetailOpen));
                OnPropertyChanged(nameof(IsMetricsOverviewOpen));
            }
        }
    }

    public bool IsMetricsDetailOpen => _selectedMetricsCamera != null;
    public bool IsMetricsOverviewOpen => _selectedMetricsCamera == null;

    public int SelectedNavIndex
    {
        get => _selectedNavIndex;
        set
        {
            var old = _selectedNavIndex;
            if (SetProperty(ref _selectedNavIndex, value))
            {
                if (old == 8 && value != 8 && SelectedDetailCamera != null)
                {
                    _ = SelectedDetailCamera.StopStreamingAsync();
                }
                if (old == 2 && value != 2 && SelectedMetricsCamera != null)
                {
                    _ = SelectedMetricsCamera.StopStreamingAsync();
                    SelectedMetricsCamera = null;
                }

                OnPropertyChanged(nameof(IsOverviewSelected));
                OnPropertyChanged(nameof(IsTrainingSelected));
                if (value == 10) TrainingLab.RefreshCommand.Execute(null);
                OnPropertyChanged(nameof(IsCamerasSelected));
                OnPropertyChanged(nameof(IsMetricsSelected));
                OnPropertyChanged(nameof(IsAlertsSelected));
                OnPropertyChanged(nameof(IsIntegrationsSelected));
                OnPropertyChanged(nameof(IsModelsSelected));
                OnPropertyChanged(nameof(IsSystemSelected));
                OnPropertyChanged(nameof(IsWebConnectionSelected));
                OnPropertyChanged(nameof(IsDiagnosticsSelected));
                OnPropertyChanged(nameof(IsCameraDetailSelected));
                OnPropertyChanged(nameof(IsNavAnalyticsSelected));
                OnPropertyChanged(nameof(IsNavRulesSelected));
                OnPropertyChanged(nameof(IsNavEventsSelected));
                OnPropertyChanged(nameof(IsSettingsOrTechnicalSelected));
                OnPropertyChanged(nameof(IsPerformanceSettingsSelected));

                if (value == 5) SettingsTabIndex = 0;
                else if (value == 9) SettingsTabIndex = 1;
                else if (value == 6) SettingsTabIndex = 2;
                else if (value == 4) SettingsTabIndex = 3;
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
    public bool IsMetricsSelected
    {
        get => _selectedNavIndex == 2;
        set { if (value) SelectedNavIndex = 2; }
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
    public bool IsPerformanceSettingsSelected
    {
        get => _selectedNavIndex == 9;
        set { if (value) SelectedNavIndex = 9; }
    }
    public bool IsSettingsOrTechnicalSelected => _selectedNavIndex is 4 or 5 or 6 or 9;

    public CameraViewModel? SelectedDetailCamera
    {
        get => _selectedDetailCamera;
        set
        {
            if (SetProperty(ref _selectedDetailCamera, value))
            {
                OnPropertyChanged(nameof(IsNavAnalyticsSelected));
                OnPropertyChanged(nameof(IsNavRulesSelected));
                OnPropertyChanged(nameof(IsNavEventsSelected));
            }
        }
    }

    public bool IsNavAnalyticsSelected => IsCameraDetailSelected && SelectedDetailCamera?.IsAnalyticsTabSelected == true;
    public bool IsNavRulesSelected => IsCameraDetailSelected && SelectedDetailCamera?.IsRulesTabSelected == true;
    public bool IsNavEventsSelected => IsCameraDetailSelected && SelectedDetailCamera?.IsEventsTabSelected == true;

    public bool ShowPostSaveBanner
    {
        get => _showPostSaveBanner;
        set => SetProperty(ref _showPostSaveBanner, value);
    }
    private bool _showPostSaveBanner;

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

    private bool _isInitialSyncComplete;
    public bool IsInitialSyncComplete
    {
        get => _isInitialSyncComplete;
        private set
        {
            if (SetProperty(ref _isInitialSyncComplete, value))
            {
                OnPropertyChanged(nameof(HasCameras));
                OnPropertyChanged(nameof(HasNoCameras));
                OnPropertyChanged(nameof(IsLoadingInitialState));
                OnPropertyChanged(nameof(ShowFirstRunExperience));
                OnPropertyChanged(nameof(ShowApplicationShell));
            }
        }
    }

    // Empty State Helpers
    public bool HasCameras => _isInitialSyncComplete && Cameras.Count > 0;
    public bool HasNoCameras => _isInitialSyncComplete && Cameras.Count == 0;
    public bool IsLoadingInitialState => !_isInitialSyncComplete;

    public bool ShowFirstRunExperience => _isInitialSyncComplete && (Cameras.Count == 0 || (!_isFirstRunCompleted && _firstRunStep == 6));
    public bool ShowApplicationShell => _isInitialSyncComplete && Cameras.Count > 0 && (_isFirstRunCompleted || _firstRunStep != 6);

    public void MarkInitialSyncComplete()
    {
        IsInitialSyncComplete = true;
        OnPropertyChanged(nameof(ShowFirstRunExperience));
        OnPropertyChanged(nameof(ShowApplicationShell));
    }

    // First Run Experience State & Properties
    public int FirstRunStep
    {
        get => _firstRunStep;
        set
        {
            if (SetProperty(ref _firstRunStep, value))
            {
                OnPropertyChanged(nameof(IsFirstRunStep1));
                OnPropertyChanged(nameof(IsFirstRunStep2));
                OnPropertyChanged(nameof(IsFirstRunStep3));
                OnPropertyChanged(nameof(IsFirstRunStep4));
                OnPropertyChanged(nameof(IsFirstRunStep5));
                OnPropertyChanged(nameof(IsFirstRunStep6));
                OnPropertyChanged(nameof(ShowFirstRunExperience));
                OnPropertyChanged(nameof(ShowApplicationShell));
            }
        }
    }

    public bool IsFirstRunStep1 => _firstRunStep == 1;
    public bool IsFirstRunStep2 => _firstRunStep == 2;
    public bool IsFirstRunStep3 => _firstRunStep == 3;
    public bool IsFirstRunStep4 => _firstRunStep == 4;
    public bool IsFirstRunStep5 => _firstRunStep == 5;
    public bool IsFirstRunStep6 => _firstRunStep == 6;

    public CameraViewModel? SavedFirstRunCamera
    {
        get => _savedFirstRunCamera;
        set => SetProperty(ref _savedFirstRunCamera, value);
    }

    public int SettingsTabIndex
    {
        get => _settingsTabIndex;
        set => SetProperty(ref _settingsTabIndex, value);
    }

    public ObservableCollection<string> AccelerationModes { get; } =
        ["Automático (Recomendado)", "GPU DirectML", "CPU"];
    private string _selectedAccelerationMode = "Automático (Recomendado)";
    public string SelectedAccelerationMode
    {
        get => _selectedAccelerationMode;
        set
        {
            if (SetProperty(ref _selectedAccelerationMode, value))
            {
                _ = ApplyAccelerationModeAsync(value);
            }
        }
    }

    public ObservableCollection<string> PerformanceProfiles { get; } =
        ["Equilibrado (Recomendado)", "Eficiencia (Bajo consumo)", "Rendimiento máximo"];
    private string _selectedPerformanceProfile = "Equilibrado (Recomendado)";
    public string SelectedPerformanceProfile
    {
        get => _selectedPerformanceProfile;
        set
        {
            if (SetProperty(ref _selectedPerformanceProfile, value))
            {
                _ = ApplyPerformanceProfileAsync(value);
            }
        }
    }
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

    public bool IsProbeStreaming
    {
        get => _isProbeStreaming;
        private set => SetProperty(ref _isProbeStreaming, value);
    }

    public double ProbeFps
    {
        get => _probeFps;
        private set => SetProperty(ref _probeFps, value);
    }

    public string ProbeFpsText
    {
        get => _probeFpsText;
        private set => SetProperty(ref _probeFpsText, value);
    }

    public string ProbeResolutionText
    {
        get => _probeResolutionText;
        private set => SetProperty(ref _probeResolutionText, value);
    }

    public string ProbeLatencyText
    {
        get => _probeLatencyText;
        private set => SetProperty(ref _probeLatencyText, value);
    }

    public string EffectiveWizardSource => _wizardSourceType switch
    {
        "USB" => _wizardUsbIndex,
        "RTSP" => _wizardRtspUrl,
        "File" => _wizardFilePath,
        _ => _wizardUsbIndex
    };

    public bool IsTestingConnection
    {
        get => _isTestingConnection;
        private set
        {
            if (SetProperty(ref _isTestingConnection, value))
            {
                OnPropertyChanged(nameof(IsNotTestingConnection));
            }
        }
    }
    public bool IsNotTestingConnection => !_isTestingConnection;
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
    public string? TestTechnicalDetails { get => _testTechnicalDetails; private set { if (SetProperty(ref _testTechnicalDetails, value)) OnPropertyChanged(nameof(HasTechnicalDetails)); } }
    public bool HasTechnicalDetails => !string.IsNullOrWhiteSpace(_testTechnicalDetails);

    public bool AllowSaveWithoutProbe
    {
        get => _allowSaveWithoutProbe;
        set
        {
            if (SetProperty(ref _allowSaveWithoutProbe, value))
            {
                OnPropertyChanged(nameof(CanProceedFromStep3));
            }
        }
    }

    public bool CanProceedFromStep3 => HasTestedSuccessfully || AllowSaveWithoutProbe;

    public string FileNameOnly => !string.IsNullOrWhiteSpace(_wizardFilePath) ? Path.GetFileName(_wizardFilePath) : "-";
    public string FileExtensionOnly => !string.IsNullOrWhiteSpace(_wizardFilePath) ? Path.GetExtension(_wizardFilePath).ToUpperInvariant() : "-";
    public bool IsFileSelected => !string.IsNullOrWhiteSpace(_wizardFilePath);
    public bool LoopVideoFile { get => _loopVideoFile; set => SetProperty(ref _loopVideoFile, value); }

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

    public IEnumerable<CameraViewModel> FilteredCameras
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SelectedCameraFilter) || SelectedCameraFilter == "Todas")
            {
                return Cameras;
            }
            return Cameras.Where(c => c.SourceType.Equals(SelectedCameraFilter, StringComparison.OrdinalIgnoreCase));
        }
    }

    public DeviceItemViewModel? SelectedDevice { get => _selectedDevice; set => SetProperty(ref _selectedDevice, value); }
    public string? SelectedCameraFilter
    {
        get => _selectedCameraFilter;
        set
        {
            if (SetProperty(ref _selectedCameraFilter, value))
            {
                OnPropertyChanged(nameof(FilteredCameras));
            }
        }
    }
    public DateTime? HistoryFrom { get => _historyFrom; set => SetProperty(ref _historyFrom, value); }
    public DateTime? HistoryTo { get => _historyTo; set => SetProperty(ref _historyTo, value); }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool IsDarkTheme { get => _isDarkTheme; private set => SetProperty(ref _isDarkTheme, value); }
    public string ThemeButtonText => IsDarkTheme ? "Tema claro" : "Tema oscuro";

    // Commands
    public IRelayCommand<object?> NavigateCommand { get; }
    public IRelayCommand NavigateToAnalyticsCommand { get; }
    public IRelayCommand NavigateToRulesCommand { get; }
    public IRelayCommand NavigateToEventsCommand { get; }
    public IRelayCommand DismissPostSaveBannerCommand { get; }
    public IRelayCommand ConfigureAiAfterSaveCommand { get; }
    public IAsyncRelayCommand<CameraViewModel> OpenCameraDetailCommand { get; }
    public IAsyncRelayCommand<string> OpenCameraByIdCommand { get; }
    public IRelayCommand BackToCamerasCommand { get; }
    public IAsyncRelayCommand<CameraViewModel> DuplicateCameraCommand { get; }
    public IAsyncRelayCommand<CameraViewModel> OpenCameraMetricsCommand { get; }
    public IAsyncRelayCommand BackToMetricsOverviewCommand { get; }
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
    public IAsyncRelayCommand RefreshUsbDevicesCommand { get; }

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

    // First Run Experience Commands
    public IRelayCommand FirstRunStartCommand { get; }
    public IRelayCommand<string> FirstRunSelectSourceCommand { get; }
    public IRelayCommand FirstRunNextCommand { get; }
    public IRelayCommand FirstRunBackCommand { get; }
    public IRelayCommand<string> FirstRunSetCameraNameSuggestionCommand { get; }
    public IAsyncRelayCommand FirstRunSaveCameraCommand { get; }
    public IRelayCommand FirstRunConfigureAiCommand { get; }
    public IRelayCommand FirstRunGoToAppCommand { get; }

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
            StatusMessage = $"Error al iniciar el sistema: {ex.Message}";
        }
        finally
        {
            IsNodeBusy = false;
            if (_hostController.CameraService is not null)
            {
                _hostController.CameraService.CameraRunningStateChanged -= OnCameraRunningStateChanged;
                _hostController.CameraService.CameraRunningStateChanged += OnCameraRunningStateChanged;
                _hostController.CameraService.CamerasChanged -= OnCamerasChanged;
                _hostController.CameraService.CamerasChanged += OnCamerasChanged;
            }
            _pollTimer.Start();
            UpdateFromHostState();
            await SyncCamerasAsync();
            IsInitialSyncComplete = true;
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
        if (SelectedMetricsCamera != null && SelectedMetricsCamera != camera)
        {
            await SelectedMetricsCamera.StopStreamingAsync();
        }
        SelectedMetricsCamera = null;
        SelectedDetailCamera = camera;
        camera.PropertyChanged -= OnDetailCameraPropertyChanged;
        camera.PropertyChanged += OnDetailCameraPropertyChanged;
        SelectedNavIndex = 8;
        await camera.InitializeZonesAsync();
        await camera.LoadAssignedAnalyticsAsync();
        await camera.RefreshEventsAsync();
        await camera.StartAsync();
    }

    private void OnDetailCameraPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CameraViewModel.IsAnalyticsTabSelected)
            or nameof(CameraViewModel.IsRulesTabSelected)
            or nameof(CameraViewModel.IsEventsTabSelected)
            or nameof(CameraViewModel.IsMonitorTabSelected))
        {
            OnPropertyChanged(nameof(IsNavAnalyticsSelected));
            OnPropertyChanged(nameof(IsNavRulesSelected));
            OnPropertyChanged(nameof(IsNavEventsSelected));
        }
    }

    private async Task OpenCameraByIdAsync(string? cameraId)
    {
        if (string.IsNullOrWhiteSpace(cameraId)) return;
        var cam = Cameras.FirstOrDefault(c => c.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
        if (cam != null)
        {
            await OpenDetailAsync(cam);
        }
    }

    private async Task OpenCameraMetricsAsync(CameraViewModel? camera)
    {
        if (camera is null) return;
        if (SelectedDetailCamera != null && SelectedDetailCamera != camera)
        {
            await SelectedDetailCamera.StopStreamingAsync();
        }
        SelectedMetricsCamera = camera;
        SelectedNavIndex = 2;
        await camera.RefreshEventsAsync();
        await camera.StartAsync();
    }

    private async Task BackToMetricsOverviewAsync()
    {
        if (SelectedMetricsCamera != null)
        {
            await SelectedMetricsCamera.StopStreamingAsync();
            SelectedMetricsCamera = null;
        }
    }

    private async Task DuplicateCameraAsync(CameraViewModel? sourceCamera)
    {
        if (sourceCamera is null || _hostController.CameraService is null) return;

        try
        {
            var newId = Guid.NewGuid().ToString("N")[..8];
            var newName = $"{sourceCamera.Name} (Copia)";
            var req = new CameraWriteRequest(
                Id: newId,
                Name: newName,
                Source: sourceCamera.Source,
                Enabled: false,
                Username: null,
                Password: null);

            await _hostController.CameraService.CreateAsync(req);

            if (sourceCamera.EditorZones.Count > 0)
            {
                await _hostController.CameraService.UpdateZonesAsync(newId, sourceCamera.EditorZones);
            }

            if (sourceCamera.EditorLines.Count > 0 && _hostController.LineStore is not null)
            {
                foreach (var line in sourceCamera.EditorLines)
                {
                    await _hostController.LineStore.SaveAsync(newId, line with { CameraId = newId, Id = Guid.NewGuid().ToString("N")[..8] });
                }
            }

            if (_hostController.AnalyticService is not null)
            {
                var existingInstances = await _hostController.AnalyticService.ListByCameraAsync(sourceCamera.Id) ?? [];
                foreach (var inst in existingInstances)
                {
                    var createReq = new CameraAnalyticWriteRequest(
                        Id: Guid.NewGuid().ToString("N")[..8],
                        AnalyticTypeId: inst.AnalyticTypeId,
                        Name: inst.Name,
                        Enabled: false,
                        Configuration: inst.Configuration,
                        AssignedZoneIds: inst.AssignedZoneIds,
                        AssignedLineIds: inst.AssignedLineIds);
                    await _hostController.AnalyticService.CreateAsync(newId, createReq);
                }
            }

            await SyncCamerasAsync();
            StatusMessage = $"Cámara duplicada como '{newName}'. Configuración en estado detenido.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al duplicar cámara: {ex.Message}";
        }
    }

    private async Task StartNodeAsync()
    {
        IsNodeBusy = true;
        StatusMessage = "Iniciando sistema...";
        try
        {
            var bind = IsLanAccess ? "0.0.0.0" : "127.0.0.1";
            await _hostController.StartAsync(new NodeHostSettings(bind, ApiPort, ApiKey));
            StatusMessage = "Sistema iniciado correctamente.";
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
        StatusMessage = "Deteniendo sistema y liberando cámaras...";
        try
        {
            for (var i = 0; i < Cameras.Count; i++)
            {
                await Cameras[i].StopStreamingAsync();
            }
            await _hostController.StopAsync();
            StatusMessage = "Sistema detenido.";
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
        StatusMessage = "Reiniciando sistema...";
        try
        {
            var bind = IsLanAccess ? "0.0.0.0" : "127.0.0.1";
            await _hostController.RestartAsync(new NodeHostSettings(bind, ApiPort, ApiKey));
            StatusMessage = "Sistema reiniciado y operativo.";
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
        WizardUsbIndex = string.Empty;
        SelectedUsbDevice = null;
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
            if (SelectedUsbDevice is not null && !DetectedUsbDevices.Contains(SelectedUsbDevice))
            {
                SelectedUsbDevice = null;
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
            if (WizardSourceType == "USB")
            {
                if (!IsCustomUsbIndex && SelectedUsbDevice == null)
                {
                    AddCameraError = "Selecciona una cámara de la lista o conecta tu dispositivo y pulsa 'Actualizar dispositivos'.";
                    return;
                }
                if (IsCustomUsbIndex && (!int.TryParse(WizardUsbIndex?.Trim(), out var idx) || idx < 0))
                {
                    AddCameraError = "El índice USB debe ser un número entero mayor o igual a 0 (ej. 0, 1).";
                    return;
                }
            }
            else if (WizardSourceType == "RTSP")
            {
                if (string.IsNullOrWhiteSpace(WizardRtspUrl) ||
                    (!WizardRtspUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) &&
                     !WizardRtspUrl.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase)))
                {
                    AddCameraError = "La URL RTSP debe comenzar con 'rtsp://' o 'rtsps://' (ej. rtsp://192.168.1.50:554/stream1).";
                    return;
                }
            }
            else if (WizardSourceType == "File")
            {
                if (string.IsNullOrWhiteSpace(WizardFilePath) || !File.Exists(WizardFilePath))
                {
                    AddCameraError = "El archivo de video seleccionado no existe o no es accesible en el sistema.";
                    return;
                }
            }
            else if (!IsStep2Valid)
            {
                AddCameraError = "Configuración de origen no válida.";
                return;
            }
        }
        else if (WizardStep == 3)
        {
            if (!CanProceedFromStep3)
            {
                AddCameraError = "Debe verificar la cámara exitosamente o habilitar la opción avanzada para guardar sin probar.";
                return;
            }
        }

        if (WizardStep < 4)
        {
            if (WizardStep == 3)
            {
                _ = StopProbePreviewAsync();
            }
            WizardStep++;
            if (WizardStep == 3 && TestConnectionSuccess is null && !IsTestingConnection)
            {
                _ = StartProbePreviewAsync();
            }
        }
    }

    private void PrevWizardStep()
    {
        AddCameraError = null;
        if (WizardStep > 1)
        {
            if (WizardStep == 3)
            {
                _ = StopProbePreviewAsync();
            }
            WizardStep--;
        }
    }

    public static string TranslateProbeError(string? rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError))
            return "No se pudo abrir la cámara. Compruebe la conexión y los parámetros.";

        var lower = rawError.ToLowerInvariant();
        if (lower.Contains("timeout") || lower.Contains("timed out") || lower.Contains("tiempo de espera"))
            return "No se recibió respuesta de la cámara (Tiempo de espera agotado).";
        if (lower.Contains("unauthorized") || lower.Contains("401") || lower.Contains("auth") || lower.Contains("forbidden") || lower.Contains("403"))
            return "Usuario o contraseña incorrectos para acceder a la transmisión.";
        if (lower.Contains("busy") || lower.Contains("in use") || lower.Contains("en uso") || lower.Contains("access denied") || lower.Contains("ocupad"))
            return "La cámara está siendo utilizada por otra aplicación o proceso.";
        if (lower.Contains("not found") || lower.Contains("no such device") || lower.Contains("no existe") || lower.Contains("no encontrada"))
            return "El dispositivo o archivo de video ya no está disponible.";
        if (lower.Contains("unreachable") || lower.Contains("refused") || lower.Contains("network") || lower.Contains("host") || lower.Contains("socket"))
            return "No se pudo conectar con la dirección IP/RTSP indicada. Verifique la red y el puerto.";

        return "No se pudo establecer la transmisión de video con el origen especificado.";
    }

    private void DispatchUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        if (System.Windows.Application.Current is null || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            try { action(); } catch { }
            return;
        }

        try
        {
            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
        }
        catch { }
    }

    private Task DispatchAsync(Action action)
    {
        DispatchUi(action);
        return Task.CompletedTask;
    }

    public async Task StartProbePreviewAsync()
    {
        await StopProbePreviewAsync();

        var sessionId = Interlocked.Increment(ref _probeSessionId);

        if (_hostController.CameraService is null)
        {
            DispatchUi(() =>
            {
                TestConnectionSuccess = false;
                TestConnectionMessage = "Servicio de cámaras no disponible.";
                TestConnectionDetails = "El host no se encuentra en ejecución.";
                TestTechnicalDetails = "HostController.CameraService is null";
                TestPreviewImage = null;
                IsProbeStreaming = false;
                IsTestingConnection = false;
            });
            return;
        }

        DispatchUi(() =>
        {
            IsTestingConnection = true;
            TestConnectionSuccess = null;
            TestConnectionMessage = "Iniciando vista previa en vivo...";
            TestConnectionDetails = null;
            TestTechnicalDetails = null;
            TestPreviewImage = null;
        });

        var cts = new CancellationTokenSource();
        _probeCancellation = cts;
        var token = cts.Token;

        try
        {
            var req = new CameraTestRequest(
                Source: EffectiveWizardSource,
                Username: string.IsNullOrWhiteSpace(WizardRtspUsername) ? null : WizardRtspUsername,
                Password: string.IsNullOrWhiteSpace(WizardRtspPassword) ? null : WizardRtspPassword);

            var result = await _hostController.CameraService.ProbeStreamAsync(req, 15.0, 70, token);

            if (sessionId != _probeSessionId || token.IsCancellationRequested)
            {
                if (result.Subscription is not null)
                {
                    try { await result.Subscription.DisposeAsync(); } catch { }
                }
                return;
            }

            if (result.Status == CameraStreamStatus.Success && result.Reader is not null)
            {
                _probeSubscription = result.Subscription;
                var reader = result.Reader;

                DispatchUi(() =>
                {
                    if (sessionId != _probeSessionId) return;
                    TestConnectionSuccess = true;
                    TestConnectionMessage = "Cámara disponible y transmitiendo";
                    TestConnectionDetails = "Transmisión en vivo activa";
                    TestTechnicalDetails = null;
                    IsProbeStreaming = true;
                });

                _probePumpTask = Task.Run(async () =>
                {
                    try
                    {
                        while (await reader.WaitToReadAsync(token))
                        {
                            while (reader.TryRead(out var frame))
                            {
                                if (token.IsCancellationRequested || sessionId != _probeSessionId) break;
                                try
                                {
                                    var bitmap = DecodeJpeg(frame.Jpeg);
                                    DispatchUi(() =>
                                    {
                                        if (sessionId != _probeSessionId) return;
                                        TestPreviewImage = bitmap;
                                        ProbeFps = frame.FramesPerSecond;
                                        ProbeFpsText = $"{frame.FramesPerSecond:F1} FPS";
                                        ProbeResolutionText = $"{frame.Width}x{frame.Height}";
                                        ProbeLatencyText = $"{frame.LatencyMilliseconds:F0} ms";
                                        TestConnectionDetails = $"{frame.Width}x{frame.Height} @ {frame.FramesPerSecond:F1} FPS ({frame.LatencyMilliseconds:F0} ms)";
                                    });
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        if (sessionId == _probeSessionId)
                        {
                            DispatchUi(() =>
                            {
                                if (sessionId != _probeSessionId) return;
                                TestConnectionMessage = "Transmisión de prueba detenida";
                                TestConnectionDetails = ex.Message;
                                IsProbeStreaming = false;
                            });
                        }
                    }
                }, token);
            }
            else
            {
                DispatchUi(() =>
                {
                    if (sessionId != _probeSessionId) return;
                    TestConnectionSuccess = false;
                    TestConnectionMessage = "No se pudo abrir la cámara";
                    TestConnectionDetails = TranslateProbeError(result.Error);
                    TestTechnicalDetails = result.Error;
                    TestPreviewImage = null;
                    IsProbeStreaming = false;
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (sessionId == _probeSessionId)
            {
                DispatchUi(() =>
                {
                    if (sessionId != _probeSessionId) return;
                    TestConnectionSuccess = false;
                    TestConnectionMessage = "No se pudo abrir la cámara";
                    TestConnectionDetails = TranslateProbeError(ex.Message);
                    TestTechnicalDetails = ex.ToString();
                    TestPreviewImage = null;
                    IsProbeStreaming = false;
                });
            }
        }
        finally
        {
            if (sessionId == _probeSessionId)
            {
                DispatchUi(() =>
                {
                    if (sessionId != _probeSessionId) return;
                    IsTestingConnection = false;
                    OnPropertyChanged(nameof(CanProceedFromStep3));
                });
            }
        }
    }

    public async Task StopProbePreviewAsync()
    {
        // 1. Advance session and capture references
        Interlocked.Increment(ref _probeSessionId);

        var cts = _probeCancellation;
        _probeCancellation = null;

        var pumpTask = _probePumpTask;
        _probePumpTask = null;

        var subscription = _probeSubscription;
        _probeSubscription = null;

        // 2. Immediate state reset
        DispatchUi(() =>
        {
            IsProbeStreaming = false;
            IsTestingConnection = false;
        });

        // 3. Signal cancellation
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
        }

        // 4. Dispose subscription (closes upstream channel/feed to unblock reader)
        if (subscription is not null)
        {
            try { await subscription.DisposeAsync(); } catch { }
        }

        // 5. Await frame pump task with timeout protection (never hangs)
        if (pumpTask is not null)
        {
            try
            {
                await pumpTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
            catch { }
        }

        // 6. Dispose CTS
        if (cts is not null)
        {
            try { cts.Dispose(); } catch { }
        }

        // 7. Guaranteed final state before returning
        DispatchUi(() =>
        {
            IsProbeStreaming = false;
            IsTestingConnection = false;
        });
    }

    private async Task TestWizardConnectionAsync()
    {
        await StartProbePreviewAsync();
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

        // Stop probe before creating camera to ensure single capture ownership
        await StopProbePreviewAsync();

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
            StatusMessage = $"Cámara '{req.Name}' agregada correctamente.";

            // Navigate to camera detail immediately (Camera-first flow)
            var created = Cameras.FirstOrDefault(c => c.Id == id);
            if (created is not null)
            {
                await OpenDetailAsync(created);
                created.IsMonitorTabSelected = true;
                ShowPostSaveBanner = true;
            }
        }
        catch (Exception ex)
        {
            AddCameraError = $"Error al agregar cámara: {ex.Message}";
        }
    }

    private async Task AddCameraAsync()
    {
        await SaveWizardCameraAsync();
    }

    private void FirstRunNext()
    {
        AddCameraError = null;
        if (FirstRunStep == 2)
        {
            if (string.IsNullOrEmpty(WizardSourceType))
            {
                AddCameraError = "Selecciona de dónde viene el video para continuar.";
                return;
            }
            FirstRunStep = 3;
        }
        else if (FirstRunStep == 3)
        {
            if (WizardSourceType == "USB")
            {
                if (SelectedUsbDevice == null && (string.IsNullOrWhiteSpace(WizardUsbIndex) || !int.TryParse(WizardUsbIndex?.Trim(), out var idx) || idx < 0))
                {
                    AddCameraError = "Selecciona una cámara de la lista o especifica un índice de dispositivo USB válido.";
                    return;
                }
            }
            else if (WizardSourceType == "RTSP")
            {
                if (string.IsNullOrWhiteSpace(WizardRtspUrl) ||
                    (!WizardRtspUrl.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) &&
                     !WizardRtspUrl.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase)))
                {
                    AddCameraError = "La URL RTSP debe comenzar con 'rtsp://' o 'rtsps://' (ej. rtsp://192.168.1.50:554/stream1).";
                    return;
                }
            }
            else if (WizardSourceType == "File")
            {
                if (string.IsNullOrWhiteSpace(WizardFilePath) || !File.Exists(WizardFilePath))
                {
                    AddCameraError = "El archivo de video seleccionado no existe o no es accesible en el sistema.";
                    return;
                }
            }
            else if (!IsStep2Valid)
            {
                AddCameraError = "Configuración de origen no válida.";
                return;
            }

            FirstRunStep = 4;
            if (TestConnectionSuccess is null && !IsTestingConnection)
            {
                _ = StartProbePreviewAsync();
            }
        }
        else if (FirstRunStep == 4)
        {
            if (!CanProceedFromStep3)
            {
                AddCameraError = "Debe verificar la cámara exitosamente o habilitar la opción avanzada para guardar sin probar.";
                return;
            }
            IsProbeStreaming = false;
            IsTestingConnection = false;
            _ = StopProbePreviewAsync();
            FirstRunStep = 5;
        }
    }

    private void FirstRunBack()
    {
        AddCameraError = null;
        if (FirstRunStep > 1 && FirstRunStep < 6)
        {
            if (FirstRunStep == 4)
            {
                IsProbeStreaming = false;
                IsTestingConnection = false;
                _ = StopProbePreviewAsync();
            }
            FirstRunStep--;
        }
    }

    private async Task FirstRunSaveCameraAsync()
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

        // Cleanly stop probe capture session before creating the camera pipeline (Single Capture Ownership)
        await StopProbePreviewAsync();

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

            AddCameraError = null;
            StatusMessage = $"Cámara '{req.Name}' guardada correctamente.";

            var created = Cameras.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Cameras.LastOrDefault();
            SavedFirstRunCamera = created;
            FirstRunStep = 6;
            OnPropertyChanged(nameof(ShowFirstRunExperience));
            OnPropertyChanged(nameof(ShowApplicationShell));
        }
        catch (Exception ex)
        {
            AddCameraError = $"Error al guardar cámara: {ex.Message}";
        }
    }

    private void FirstRunConfigureAi()
    {
        IsProbeStreaming = false;
        IsTestingConnection = false;
        _ = StopProbePreviewAsync();

        var targetCamera = SavedFirstRunCamera ?? Cameras.FirstOrDefault();
        if (targetCamera is not null)
        {
            SelectedDetailCamera = targetCamera;
            targetCamera.PropertyChanged -= OnDetailCameraPropertyChanged;
            targetCamera.PropertyChanged += OnDetailCameraPropertyChanged;
            targetCamera.IsAnalyticsTabSelected = true;
            SelectedNavIndex = 8;

            _ = targetCamera.InitializeZonesAsync();
            _ = targetCamera.LoadAssignedAnalyticsAsync();
        }
        else
        {
            SelectedNavIndex = 1;
        }

        _isFirstRunCompleted = true;
        OnPropertyChanged(nameof(ShowFirstRunExperience));
        OnPropertyChanged(nameof(ShowApplicationShell));
    }

    private void FirstRunGoToApp()
    {
        _ = StopProbePreviewAsync();

        if (Cameras.Count > 0)
        {
            SelectedDetailCamera = SavedFirstRunCamera ?? Cameras[0];
            SelectedDetailCamera.PropertyChanged -= OnDetailCameraPropertyChanged;
            SelectedDetailCamera.PropertyChanged += OnDetailCameraPropertyChanged;
        }
        SelectedNavIndex = 0;

        _isFirstRunCompleted = true;
        OnPropertyChanged(nameof(ShowFirstRunExperience));
        OnPropertyChanged(nameof(ShowApplicationShell));
    }

    private async Task DeleteCameraAsync(CameraViewModel? camera)
    {
        if (camera is null || _hostController.CameraService is null) return;

        var result = MessageBox.Show(
            $"¿Está seguro de que desea eliminar permanentemente la cámara '{camera.Name}'?\n\nEsta acción eliminará de forma irreversible:\n• La configuración de captura de video\n• Sus {camera.ActiveAnalyticsCount} soluciones IA asignadas\n• {camera.EditorZones.Count} zonas poligonales delimitadas\n• {camera.EditorLines.Count} líneas virtuales de cruce\n• Las reglas de alerta vinculadas a esta cámara",
            $"Confirmar eliminación: {camera.Name}",
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

            if (Cameras.Count == 0)
            {
                _isFirstRunCompleted = false;
                FirstRunStep = 1;
            }
            OnPropertyChanged(nameof(ShowFirstRunExperience));
            OnPropertyChanged(nameof(ShowApplicationShell));

            StatusMessage = $"Cámara '{camera.Name}' eliminada correctamente.";
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
                NodeStatusText = "Sistema listo";
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

            foreach (var camStatus in snapshot.Cameras)
            {
                var vm = Cameras.FirstOrDefault(c => string.Equals(c.Id, camStatus.CameraId, StringComparison.OrdinalIgnoreCase));
                vm?.UpdateRuntimeMetrics(camStatus);
            }
        }

        if (_hostController.RuntimeState is { } runtime)
        {
            var active = runtime.Inventory.Devices.FirstOrDefault(d => d.Id == runtime.ActiveDeviceId);
            if (active is not null)
            {
                ProcessorText = $"{active.Name} · {(active.Backend == InferenceBackend.Cpu ? "CPU Fallback" : "DirectML GPU")}";
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

    private async Task ApplyAccelerationModeAsync(string modeLabel)
    {
        var modeKey = modeLabel switch
        {
            "GPU DirectML" => "gpu",
            "CPU" => "cpu",
            _ => "auto"
        };
        try
        {
            var store = new HandRaise.Infrastructure.Windows.Settings.JsonUserSettingsStore();
            var settings = await store.LoadAsync();
            await store.SaveAsync(settings with { AccelerationMode = modeKey });
            StatusMessage = $"Modo de aceleración configurado a: {modeLabel}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al guardar modo de aceleración: {ex.Message}";
        }
    }

    private async Task ApplyPerformanceProfileAsync(string profileLabel)
    {
        var (profileKey, targetInfFps, previewFps) = profileLabel switch
        {
            "Eficiencia (Bajo consumo)" => ("efficiency", 10, 15),
            "Rendimiento máximo" => ("performance", 30, 30),
            _ => ("balanced", 15, 30)
        };
        try
        {
            var store = new HandRaise.Infrastructure.Windows.Settings.JsonUserSettingsStore();
            var settings = await store.LoadAsync();
            await store.SaveAsync(settings with
            {
                PerformanceProfile = profileKey,
                TargetInferenceFps = targetInfFps,
                PreviewFps = previewFps
            });
            StatusMessage = $"Perfil de rendimiento cambiado a: {profileLabel}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al guardar perfil de rendimiento: {ex.Message}";
        }
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
        if (_hostController.CameraService is not null)
        {
            _hostController.CameraService.CameraRunningStateChanged -= OnCameraRunningStateChanged;
            _hostController.CameraService.CameraRunningStateChanged += OnCameraRunningStateChanged;
            _hostController.CameraService.CamerasChanged -= OnCamerasChanged;
            _hostController.CameraService.CamerasChanged += OnCamerasChanged;
        }

        _dispatcher.BeginInvoke(async () =>
        {
            StatusMessage = message;
            UpdateFromHostState();
            await SyncCamerasAsync();
            await RefreshAlertsAsync();
        });
    }

    private void OnCameraRunningStateChanged(string cameraId, bool isRunning)
    {
        _dispatcher.BeginInvoke(async () =>
        {
            await SyncCamerasAsync();
            UpdateFromHostState();
        });
    }

    private void OnCamerasChanged()
    {
        _dispatcher.BeginInvoke(async () =>
        {
            await SyncCamerasAsync();
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
                        _configuration.Storage.SnapshotDirectory,
                        _hostController.RuleStore,
                        _hostController.AlertStore,
                        _hostController.EventBus,
                        _hostController.ModelRegistry);

                    Cameras.Add(newVm);
                    await newVm.InitializeZonesAsync();
                    await newVm.LoadAssignedAnalyticsAsync();
                    if (view.Enabled || view.Running)
                    {
                        await newVm.StartAsync();
                    }
                }
            }
            if (Cameras.Count == 0)
            {
                _isFirstRunCompleted = false;
            }
            OnPropertyChanged(nameof(ShowFirstRunExperience));
            OnPropertyChanged(nameof(ShowApplicationShell));
            OnPropertyChanged(nameof(FilteredCameras));
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
        try
        {
            await TrainingLab.DisposeAsync();
            _hostController.StatusChanged -= OnHostStatusChanged;
            if (_hostController.CameraService is not null)
            {
                _hostController.CameraService.CameraRunningStateChanged -= OnCameraRunningStateChanged;
                _hostController.CameraService.CamerasChanged -= OnCamerasChanged;
            }
            await StopProbePreviewAsync();
            for (var i = 0; i < Cameras.Count; i++)
            {
                await Cameras[i].DisposeAsync();
            }
            Cameras.Clear();
        }
        finally { await _hostController.DisposeAsync(); }
    }
}
