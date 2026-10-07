using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandRaise.Application.Analytics;
using HandRaise.Application.Events;
using HandRaise.Application.Inference;
using HandRaise.Application.Lines;
using HandRaise.Application.Rules;
using HandRaise.Application.Storage;
using HandRaise.Application.Zones;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Lines;
using HandRaise.Domain.Rules;
using HandRaise.Domain.Zones;
using HandRaise.Host.Services;

namespace HandRaise.Desktop.ViewModels;

public sealed class CameraViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ICameraManagementService _cameraService;
    private readonly IAnalyticManagementService? _analyticService;
    private readonly IAnalyticCatalog? _analyticCatalog;
    private readonly ILineStore? _lineStore;
    private readonly ICapabilityPlanner? _capabilityPlanner;
    private readonly ICameraStore? _cameraStore;
    private readonly IHandEventRepository? _eventRepository;
    private readonly IRuleStore? _ruleStore;
    private readonly IAlertStore? _alertStore;
    private readonly HandEventBus? _eventBus;
    private readonly string _snapshotDirectory;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private readonly string _id;
    private string _name;
    private string _source;
    private readonly bool _autoStart;

    private CancellationTokenSource? _streamCancellation;
    private Task? _streamTask;
    private IAsyncDisposable? _subscription;
    private IAsyncDisposable? _eventBusSubscription;
    private Task? _eventBusTask;
    private CancellationTokenSource? _eventBusCancellation;

    private ImageSource? _videoImage;
    private string _status = "Desconectada";
    private string? _error;
    private double _framesPerSecond;
    private bool _isRunning;
    private bool _isStreaming;
    private int _frameWidth;
    private int _frameHeight;

    // Camera Detail Navigation Tab (0: Resumen, 1: Analíticas, 2: Zonas y Líneas, 3: Eventos, 4: Configuración, 5: Runtime Plan)
    private int _detailTabIndex;

    // Zones & Lines Editor State
    private bool _zonesInitialized;
    private bool _isEditingZones;
    private string _zoneName = string.Empty;
    private string? _zoneMessage;
    private NormalizedZone? _selectedZone;
    private IReadOnlyList<NormalizedZone> _editorZones = [];
    private IReadOnlyList<NormalizedPoint> _draftPoints = [];
    private readonly Stack<IReadOnlyList<NormalizedZone>> _zoneHistory = new();
    private bool _dragSnapshotTaken;

    // Lines Editor State
    private IReadOnlyList<LineDefinition> _editorLines = [];
    private LineDefinition? _selectedLine;
    private string _newLineName = "Línea 1";
    private LineDirectionMode _selectedLineDirection = LineDirectionMode.Bidirectional;
    private string? _lineMessage;

    // Catalog & Analytics
    private AnalyticDefinition? _selectedCatalogAnalytic;
    private bool _isAddAiModalOpen;
    private int _aiWizardStep = 1;
    private string _selectedAiCategory = "Todas";
    private string _aiCustomName = string.Empty;
    private double _aiSensitivity = 0.5;
    private double _aiMinConfidence = 0.5;
    private bool _aiSaveEvidence = true;
    private bool _showAdvancedAiParams;
    private string? _selectedTargetZoneName;
    private string? _selectedTargetLineId;
    private bool _aiSuccessBannerVisible;
    private string? _aiSuccessMessage;

    // Camera Configuration Editing
    private string _editName;
    private string _editSource;
    private bool _editEnabled;
    private string? _configMessage;
    private bool _isToggling;
    private string? _addAiError;

    // Rules Modal State
    private bool _isRulesModalOpen;
    private bool _isEditingRule;
    private CameraAnalyticItemViewModel? _selectedRuleAnalytic;
    private string? _ruleEditId;
    private string _ruleEditName = string.Empty;
    private string? _ruleEditEventType;
    private string _ruleEditSeverity = "Alta";
    private int _ruleEditCooldownSeconds = 5;
    private bool _ruleEditEnabled = true;
    private string? _ruleErrorMessage;
    private int _isRenderingFrame;

    // Runtime Plan
    private string _runtimePlanSummary = "Sin soluciones IA activas";
    private string _requiredCapabilitiesSummary = "-";
    private string _modelProviderSummary = "-";

    public CameraViewModel(
        CameraView camera,
        ICameraManagementService cameraService,
        Dispatcher dispatcher,
        IAnalyticManagementService? analyticService = null,
        IAnalyticCatalog? analyticCatalog = null,
        ILineStore? lineStore = null,
        ICapabilityPlanner? capabilityPlanner = null,
        ICameraStore? cameraStore = null,
        IHandEventRepository? eventRepository = null,
        string snapshotDirectory = "data/snapshots",
        IRuleStore? ruleStore = null,
        IAlertStore? alertStore = null,
        HandEventBus? eventBus = null)
    {
        _id = camera.Id;
        _name = camera.Name;
        _source = camera.Source;
        _autoStart = camera.Enabled;
        _editName = camera.Name;
        _editSource = camera.Source;
        _editEnabled = camera.Enabled;

        _cameraService = cameraService;
        _analyticService = analyticService;
        _analyticCatalog = analyticCatalog;
        _lineStore = lineStore;
        _capabilityPlanner = capabilityPlanner;
        _cameraStore = cameraStore;
        _eventRepository = eventRepository;
        _ruleStore = ruleStore;
        _alertStore = alertStore;
        _eventBus = eventBus;
        _snapshotDirectory = snapshotDirectory;
        _dispatcher = dispatcher;

        AssignedAnalytics = [];
        AvailableCatalog = [];
        FilteredCatalog = [];
        DynamicParameters = [];
        AiCategories = ["Todas", "General", "Seguridad", "Operaciones", "Logística"];
        CameraEvents = [];
        CameraAlerts = [];
        CameraRules = [];
        ConfiguredRules = [];
        ConfiguredAnalyticsMetrics = [];
        AvailableRuleEventTypes = [];
        AvailableRuleEventOptions = [];

        UpdateFromView(camera);
        StartEventBusSubscription();

        ToggleCommand = new AsyncRelayCommand(ToggleAsync);
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync);
        SnapshotCommand = new AsyncRelayCommand(TakeSnapshotAsync);
        ToggleZoneEditorCommand = new RelayCommand(ToggleZoneEditor);
        CloseZoneCommand = new AsyncRelayCommand(CloseZoneAsync);
        UndoZoneCommand = new AsyncRelayCommand(UndoZoneAsync);
        DeleteZoneCommand = new AsyncRelayCommand(DeleteZoneAsync);
        NewZoneCommand = new RelayCommand(StartNewZone);

        // Lines commands
        AddLineCommand = new AsyncRelayCommand(AddLineAsync);
        DeleteLineCommand = new AsyncRelayCommand(DeleteLineAsync);

        // Analytics commands
        AddAnalyticCommand = new AsyncRelayCommand(AddAnalyticAsync);
        RefreshAnalyticsCommand = new AsyncRelayCommand(LoadAssignedAnalyticsAsync);
        RemoveAnalyticCommand = new AsyncRelayCommand<CameraAnalyticItemViewModel?>(RemoveAnalyticAsync);

        // AI Wizard commands
        OpenAddAiModalCommand = new RelayCommand(OpenAddAiModal);
        CloseAddAiModalCommand = new RelayCommand(CloseAddAiModal);
        NextAiWizardStepCommand = new RelayCommand(NextAiWizardStep);
        PrevAiWizardStepCommand = new RelayCommand(PrevAiWizardStep);
        ConfirmAddAiSolutionCommand = new AsyncRelayCommand(ConfirmAddAiSolutionAsync);
        StartContextualZoneCreationCommand = new RelayCommand(StartContextualZoneCreation);
        StartContextualRectangleCreationCommand = new RelayCommand(StartContextualRectangleCreation);
        StartContextualLineCreationCommand = new RelayCommand(StartContextualLineCreation);
        SaveContextualZoneAndReturnCommand = new AsyncRelayCommand(SaveContextualZoneAndReturnAsync);
        SaveContextualLineAndReturnCommand = new AsyncRelayCommand(SaveContextualLineAndReturnAsync);
        CancelContextualGeometryAndReturnCommand = new RelayCommand(CancelContextualGeometryAndReturn);
        CreateZoneInlineCommand = new AsyncRelayCommand(CreateZoneInlineAsync);
        CreateLineInlineCommand = new AsyncRelayCommand(CreateLineInlineAsync);
        GoToRulesCommand = new RelayCommand(OpenRulesModal);
        GoToMonitorCommand = new RelayCommand(() => { IsMonitorTabSelected = true; AiSuccessBannerVisible = false; });
        DismissAiSuccessBannerCommand = new RelayCommand(() => AiSuccessBannerVisible = false);

        // Rules modal commands
        OpenRulesModalCommand = new RelayCommand(OpenRulesModal);
        CloseRulesModalCommand = new RelayCommand(CloseRulesModal);
        StartCreateRuleCommand = new RelayCommand(StartCreateRule);
        StartEditRuleCommand = new RelayCommand<AlertRuleItemViewModel?>(StartEditRule);
        StartCreateRuleFromWorkspaceCommand = new RelayCommand(StartCreateRuleFromWorkspace);
        StartEditRuleFromWorkspaceCommand = new RelayCommand<AlertRuleItemViewModel?>(StartEditRuleFromWorkspace);
        CancelEditRuleCommand = new RelayCommand(CancelEditRule);
        SaveRuleCommand = new AsyncRelayCommand(SaveRuleAsync);
        DeleteRuleCommand = new AsyncRelayCommand<AlertRuleItemViewModel?>(DeleteRuleAsync);

        // UX10 Unified Workspace & Direct Geometry commands
        ToggleDangerZoneCommand = new RelayCommand(() => IsDangerZoneExpanded = !IsDangerZoneExpanded);
        StartDirectZoneCreationCommand = new RelayCommand(StartDirectZoneCreation);
        StartDirectRectangleCreationCommand = new RelayCommand(StartDirectRectangleCreation);
        StartDirectLineCreationCommand = new RelayCommand(StartDirectLineCreation);
        DeleteZoneItemCommand = new AsyncRelayCommand<NormalizedZone?>(DeleteZoneItemAsync);
        DeleteLineItemCommand = new AsyncRelayCommand<LineDefinition?>(DeleteLineItemAsync);
        SelectCatalogAnalyticAndNextCommand = new RelayCommand<object?>(SelectCatalogAnalyticAndNext);

        // Tab selection commands
        SelectMonitorTabCommand = new RelayCommand(() => IsMonitorTabSelected = true);
        SelectAnalyticsTabCommand = new RelayCommand(() => IsAnalyticsTabSelected = true);
        SelectSpacesTabCommand = new RelayCommand(() => IsSpacesTabSelected = true);
        SelectRulesTabCommand = new RelayCommand(() => IsRulesTabSelected = true);
        SelectEventsTabCommand = new RelayCommand(() => IsEventsTabSelected = true);
        SelectSettingsTabCommand = new RelayCommand(() => IsSettingsTabSelected = true);
        OpenConfigTabCommand = new RelayCommand(() => IsSettingsTabSelected = true);

        // Config commands
        SaveConfigCommand = new AsyncRelayCommand(SaveConfigAsync);
        TestConfigConnectionCommand = new AsyncRelayCommand(TestConfigConnectionAsync);
        RefreshEventsCommand = new AsyncRelayCommand(RefreshEventsAsync);
    }

    public string Id => _id;
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string Source => _source;
    public string SourceDescription => SanitizeSource(_source);
    public string SourceType => DetectSourceType(_source);
    public bool AutoStart => _autoStart;

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasStreamError));
                OnPropertyChanged(nameof(IsConnecting));
            }
        }
    }
    public double FramesPerSecond { get => _framesPerSecond; private set => SetProperty(ref _framesPerSecond, value); }
    private double _inferenceFps;
    public double InferenceFps { get => _inferenceFps; private set => SetProperty(ref _inferenceFps, value); }
    private double _inferenceLatencyMs;
    public double InferenceLatencyMs { get => _inferenceLatencyMs; private set => SetProperty(ref _inferenceLatencyMs, value); }
    private long _droppedInferenceFrames;
    public long DroppedInferenceFrames { get => _droppedInferenceFrames; private set => SetProperty(ref _droppedInferenceFrames, value); }
    private int _queueDepth;
    public int QueueDepth { get => _queueDepth; private set => SetProperty(ref _queueDepth, value); }

    public void UpdateRuntimeMetrics(HandRaise.Application.Pipelines.CameraRuntimeStatus status)
    {
        InferenceFps = Math.Round(status.InferenceFps, 1);
        InferenceLatencyMs = Math.Round(status.AverageInferenceMilliseconds, 1);
        DroppedInferenceFrames = status.DroppedInferenceFrames;
        QueueDepth = status.QueueDepth;
    }

    public ImageSource? VideoImage
    {
        get => _videoImage;
        private set
        {
            if (SetProperty(ref _videoImage, value))
            {
                OnPropertyChanged(nameof(IsStreaming));
                OnPropertyChanged(nameof(IsConnecting));
            }
        }
    }
    public int FrameWidth { get => _frameWidth; private set => SetProperty(ref _frameWidth, value); }
    public int FrameHeight { get => _frameHeight; private set => SetProperty(ref _frameHeight, value); }
    public int OpenAlertsCount => CameraAlerts.Count(a => a.Status == AlertStatus.Open);
    public int CriticalAlertsCount => CameraAlerts.Count(a => a.Severity == AlertSeverity.Critical);
    public bool HasCameraAlerts => CameraAlerts.Count > 0;
    public bool HasNoCameraAlerts => CameraAlerts.Count == 0;
    public string LastAlertText => CameraAlerts.FirstOrDefault() is { } a
        ? $"{a.Title} ({a.CreatedAtFormatted})"
        : "Sin alertas registradas";

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(ButtonText));
                OnPropertyChanged(nameof(IsStreaming));
                OnPropertyChanged(nameof(IsConnecting));
                OnPropertyChanged(nameof(IsStopped));
            }
        }
    }

    public string ButtonText => IsRunning ? "Detener" : "Volver a activar";
    public bool IsStreaming => IsRunning && string.IsNullOrEmpty(Error) && (_videoImage is not null || _status == "En línea");
    public bool IsConnecting => IsRunning && string.IsNullOrEmpty(Error) && _videoImage is null && _status != "En línea";
    public bool IsStopped => !IsRunning;
    public bool HasStreamError => !string.IsNullOrEmpty(Error);
    public bool IsReconnecting => IsRunning && ((_isStreaming || _lastFrameTime is not null) && !string.IsNullOrEmpty(Error) || _status.Contains("reconectando", StringComparison.OrdinalIgnoreCase));
    public int ActiveAnalyticsCount => AssignedAnalytics.Count(a => a.Enabled);

    // Live Overlays toggles
    private bool _showDetectionsOverlay = true;
    private bool _showZonesOverlay = true;
    private bool _showLinesOverlay = true;

    public bool ShowDetectionsOverlay { get => _showDetectionsOverlay; set => SetProperty(ref _showDetectionsOverlay, value); }
    public bool ShowZonesOverlay { get => _showZonesOverlay; set => SetProperty(ref _showZonesOverlay, value); }
    public bool ShowLinesOverlay { get => _showLinesOverlay; set => SetProperty(ref _showLinesOverlay, value); }

    public IRelayCommand SelectMonitorTabCommand { get; }
    public IRelayCommand SelectAnalyticsTabCommand { get; }
    public IRelayCommand SelectSpacesTabCommand { get; }
    public IRelayCommand SelectRulesTabCommand { get; }
    public IRelayCommand SelectEventsTabCommand { get; }
    public IRelayCommand SelectSettingsTabCommand { get; }

    // Pre-frozen static brushes to eliminate allocation & GC pressure during rendering
    private static readonly SolidColorBrush OnlineBrush = new(Color.FromRgb(63, 185, 80));
    private static readonly SolidColorBrush ConnectingBrush = new(Color.FromRgb(210, 153, 34));
    private static readonly SolidColorBrush ReconnectingBrush = new(Color.FromRgb(240, 136, 62));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(248, 81, 73));
    private static readonly SolidColorBrush StoppedBrush = new(Color.FromRgb(110, 118, 129));

    static CameraViewModel()
    {
        OnlineBrush.Freeze();
        ConnectingBrush.Freeze();
        ReconnectingBrush.Freeze();
        ErrorBrush.Freeze();
        StoppedBrush.Freeze();
    }

    public string CanonicalStateText
    {
        get
        {
            if (HasStreamError) return "Error";
            if (!IsRunning) return "Detenida";
            if (IsReconnecting) return "Reconectando";
            if (IsConnecting) return "Conectando";
            return "En línea";
        }
    }

    public string CanonicalStateIcon => CanonicalStateText switch
    {
        "En línea" => "🟢",
        "Conectando" => "⏳",
        "Reconectando" => "🔄",
        "Error" => "⚠️",
        _ => "⏹"
    };

    public Brush CanonicalStateBrush => CanonicalStateText switch
    {
        "En línea" => OnlineBrush,
        "Conectando" => ConnectingBrush,
        "Reconectando" => ReconnectingBrush,
        "Error" => ErrorBrush,
        _ => StoppedBrush
    };

    public string ContextualDiagnosticReason
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_error)) return "Fuente de video disponible y en ejecución.";
            var err = _error.ToLowerInvariant();
            if (err.Contains("timeout") || err.Contains("timed out"))
                return "Tiempo de espera agotado al conectar con el sensor o stream RTSP.";
            if (err.Contains("unreachable") || err.Contains("refused") || err.Contains("no se puede establecer una conexi"))
                return "La dirección IP o URL de la cámara no responde en la red local.";
            if (err.Contains("auth") || err.Contains("401") || err.Contains("403") || err.Contains("password") || err.Contains("credential"))
                return "Credenciales de acceso no autorizadas o inválidas.";
            if (err.Contains("busy") || err.Contains("in use") || err.Contains("ocupado") || err.Contains("already open"))
                return "El dispositivo de captura está siendo utilizado por otra aplicación.";
            if (err.Contains("not found") || err.Contains("no existe") || err.Contains("no se encontr"))
                return "No se detectó el dispositivo físico en el sistema.";
            return _error;
        }
    }

    private DateTimeOffset? _lastFrameTime;
    public string? LastFrameAgoText => _lastFrameTime is null ? null : $"Último cuadro recibido hace {Math.Max(0, (int)(DateTimeOffset.UtcNow - _lastFrameTime.Value).TotalSeconds)} s";

    // Detail Tabs (0: Monitor, 1: Soluciones IA, 2: Espacios, 3: Reglas, 4: Eventos, 5: Configuración)
    public int DetailTabIndex
    {
        get => _detailTabIndex;
        set
        {
            if (SetProperty(ref _detailTabIndex, value))
            {
                OnPropertyChanged(nameof(IsMonitorTabSelected));
                OnPropertyChanged(nameof(IsResumenTabSelected));
                OnPropertyChanged(nameof(IsAnalyticsTabSelected));
                OnPropertyChanged(nameof(IsSpacesTabSelected));
                OnPropertyChanged(nameof(IsZonesTabSelected));
                OnPropertyChanged(nameof(IsRulesTabSelected));
                OnPropertyChanged(nameof(IsEventsTabSelected));
                OnPropertyChanged(nameof(IsSettingsTabSelected));
                OnPropertyChanged(nameof(IsConfigTabSelected));
                OnPropertyChanged(nameof(IsRuntimePlanTabSelected));

                // Contextual background sync on tab switch
                if (value == 1) _ = LoadAssignedAnalyticsAsync();
                else if (value == 2) _ = InitializeZonesAsync();
                else if (value == 4) _ = RefreshEventsAsync();
            }
        }
    }

    public bool IsMonitorTabSelected { get => _detailTabIndex == 0; set { if (value) DetailTabIndex = 0; } }
    public bool IsResumenTabSelected { get => _detailTabIndex == 0; set { if (value) DetailTabIndex = 0; } }
    public bool IsAnalyticsTabSelected { get => _detailTabIndex == 1; set { if (value) DetailTabIndex = 1; } }
    public bool IsSpacesTabSelected { get => _detailTabIndex == 2; set { if (value) DetailTabIndex = 2; } }
    public bool IsZonesTabSelected { get => _detailTabIndex == 2; set { if (value) DetailTabIndex = 2; } }
    public bool IsRulesTabSelected { get => _detailTabIndex == 3; set { if (value) DetailTabIndex = 3; } }
    public bool IsEventsTabSelected { get => _detailTabIndex == 4; set { if (value) DetailTabIndex = 4; } }
    public bool IsSettingsTabSelected { get => _detailTabIndex == 5; set { if (value) DetailTabIndex = 5; } }
    public bool IsConfigTabSelected { get => _detailTabIndex == 5; set { if (value) DetailTabIndex = 5; } }
    public bool IsRuntimePlanTabSelected { get => _detailTabIndex == 5; set { if (value) DetailTabIndex = 5; } }

    public IAsyncRelayCommand ReconnectCommand { get; }
    public IAsyncRelayCommand SnapshotCommand { get; }
    public IAsyncRelayCommand ToggleRunningCommand => ToggleCommand;
    public string ActionButtonText => ButtonText;
    public string SourceSummary => SourceDescription;
    public string ActiveAnalyticsSummary => ActiveAnalyticsCount == 0 ? "Sin analíticas activas" : $"{ActiveAnalyticsCount} analítica{(ActiveAnalyticsCount == 1 ? "" : "s")} activa{(ActiveAnalyticsCount == 1 ? "" : "s")}";
    public bool HasConfiguredAnalytics => HasAssignedAnalytics;
    public bool HasNoConfiguredAnalytics => HasNoAssignedAnalytics;
    public string ActivitySummaryText => HasNoConfiguredAnalytics
        ? "⚪ Sin análisis activo — Transmitiendo video en vivo sin carga de inferencia."
        : ActiveAnalyticsCount > 0
            ? $"🟢 Análisis activo — {ActiveAnalyticsCount} {(ActiveAnalyticsCount == 1 ? "solución" : "soluciones")} IA en ejecución."
            : "🟡 Análisis pausado — Soluciones IA en pausa.";
    public string OperationalResultsSummary => HasNoConfiguredAnalytics
        ? "⚪ Sin análisis activo"
        : ActiveAnalyticsCount > 0
            ? $"🟢 {ActiveAnalyticsCount} {(ActiveAnalyticsCount == 1 ? "solución activa" : "soluciones activas")}"
            : "🟡 Soluciones en pausa";

    public string LastActivityText
    {
        get
        {
            var first = CameraEvents.FirstOrDefault();
            if (first is null) return "Sin actividad registrada";
            return $"{first.Title} · {first.TimestampText}";
        }
    }

    public int TotalDetectionsCount => CameraEvents.Count;
    public int ActiveRulesCount => CameraRules.Count(r => r.Enabled);

    public int HandRaiseEventsCount => CameraEvents.Count(e => e.RawEventType is "hand_raised" or "hand_lowered");
    public int PersonPresenceEventsCount => CameraEvents.Count(e => e.RawEventType.Contains("presence", StringComparison.OrdinalIgnoreCase));
    public int ZoneIntrusionEventsCount => CameraEvents.Count(e => e.RawEventType.Contains("intrusion", StringComparison.OrdinalIgnoreCase));
    public int LineCrossingEventsCount => CameraEvents.Count(e => e.RawEventType.Contains("line", StringComparison.OrdinalIgnoreCase));
    public int PersonCountingEventsCount => CameraEvents.Count(e => e.RawEventType.Contains("count", StringComparison.OrdinalIgnoreCase) || e.RawEventType.Contains("occupancy", StringComparison.OrdinalIgnoreCase));

    private bool _isDangerZoneExpanded;
    public bool IsDangerZoneExpanded { get => _isDangerZoneExpanded; set => SetProperty(ref _isDangerZoneExpanded, value); }
    public IRelayCommand ToggleDangerZoneCommand { get; }

    private bool _showOverlayZones = true;
    public bool ShowOverlayZones { get => _showOverlayZones; set => SetProperty(ref _showOverlayZones, value); }

    private bool _showOverlayDetections = true;
    public bool ShowOverlayDetections { get => _showOverlayDetections; set => SetProperty(ref _showOverlayDetections, value); }

    private bool _showOverlayLines = true;
    public bool ShowOverlayLines { get => _showOverlayLines; set => SetProperty(ref _showOverlayLines, value); }

    public IRelayCommand StartDirectZoneCreationCommand { get; }
    public IRelayCommand StartDirectRectangleCreationCommand { get; }
    public IRelayCommand StartDirectLineCreationCommand { get; }
    public IAsyncRelayCommand<NormalizedZone?> DeleteZoneItemCommand { get; }
    public IAsyncRelayCommand<LineDefinition?> DeleteLineItemCommand { get; }
    public IRelayCommand<object?> SelectCatalogAnalyticAndNextCommand { get; }
    public ObservableCollection<CameraAnalyticItemViewModel> ConfiguredAnalytics => AssignedAnalytics;
    public ObservableCollection<EventItemViewModel> RecentEvents => CameraEvents;
    public ObservableCollection<EventItemViewModel> Events => CameraEvents;
    public ObservableCollection<AlertItemViewModel> Alerts => CameraAlerts;
    public bool HasEvents => CameraEvents.Count > 0;
    public bool HasNoEvents => CameraEvents.Count == 0;
    public IReadOnlyList<string> LineDirectionOptions { get; } = ["Bidireccional", "A → B", "B → A"];

    // Collections
    public ObservableCollection<CameraAnalyticItemViewModel> AssignedAnalytics { get; }
    public ObservableCollection<AnalyticDefinition> AvailableCatalog { get; }
    public ObservableCollection<AnalyticDefinition> FilteredCatalog { get; }
    public ObservableCollection<AnalyticParameterItemViewModel> DynamicParameters { get; }
    public ObservableCollection<string> AiCategories { get; }
    public ObservableCollection<EventItemViewModel> CameraEvents { get; }
    public ObservableCollection<AlertItemViewModel> CameraAlerts { get; }

    // AI Solution Multi-Step Wizard State
    public bool IsAddAiModalOpen
    {
        get => _isAddAiModalOpen;
        set => SetProperty(ref _isAddAiModalOpen, value);
    }

    public int AiWizardStep
    {
        get => _aiWizardStep;
        set
        {
            if (SetProperty(ref _aiWizardStep, value))
            {
                OnPropertyChanged(nameof(IsAiWizardStep1));
                OnPropertyChanged(nameof(IsAiWizardStep2));
                OnPropertyChanged(nameof(IsAiWizardStep3));
                OnPropertyChanged(nameof(IsAiWizardStep4));
            }
        }
    }

    public bool IsAiWizardStep1 => AiWizardStep == 1;
    public bool IsAiWizardStep2 => AiWizardStep == 2;
    public bool IsAiWizardStep3 => AiWizardStep == 3;
    public bool IsAiWizardStep4 => AiWizardStep == 4;

    public string SelectedAiCategory
    {
        get => _selectedAiCategory;
        set
        {
            if (SetProperty(ref _selectedAiCategory, value))
            {
                FilterCatalog();
            }
        }
    }

    public AnalyticDefinition? SelectedCatalogAnalytic
    {
        get => _selectedCatalogAnalytic;
        set
        {
            if (SetProperty(ref _selectedCatalogAnalytic, value))
            {
                if (value != null)
                {
                    AiCustomName = value.DisplayName;
                    SetupDynamicParameters(value);
                }
                OnPropertyChanged(nameof(RequiresZone));
                OnPropertyChanged(nameof(AllowsOptionalZone));
                OnPropertyChanged(nameof(RequiresLine));
                OnPropertyChanged(nameof(HasEvidenceFeature));
            }
        }
    }

    public string AiCustomName { get => _aiCustomName; set => SetProperty(ref _aiCustomName, value); }
    public double AiSensitivity { get => _aiSensitivity; set => SetProperty(ref _aiSensitivity, value); }
    public double AiMinConfidence { get => _aiMinConfidence; set => SetProperty(ref _aiMinConfidence, value); }
    public bool AiSaveEvidence { get => _aiSaveEvidence; set => SetProperty(ref _aiSaveEvidence, value); }
    public bool ShowAdvancedAiParams { get => _showAdvancedAiParams; set => SetProperty(ref _showAdvancedAiParams, value); }
    public string? SelectedTargetZoneName { get => _selectedTargetZoneName; set => SetProperty(ref _selectedTargetZoneName, value); }
    public string? SelectedTargetLineId { get => _selectedTargetLineId; set => SetProperty(ref _selectedTargetLineId, value); }

    // Area of Analysis selection (Toda la imagen vs Área específica)
    private bool _isFullImageAnalysis = true;
    public bool IsFullImageAnalysis
    {
        get => _isFullImageAnalysis;
        set
        {
            if (SetProperty(ref _isFullImageAnalysis, value))
            {
                if (value)
                {
                    _isSpecificAreaAnalysis = false;
                    OnPropertyChanged(nameof(IsSpecificAreaAnalysis));
                }
            }
        }
    }

    private bool _isSpecificAreaAnalysis;
    public bool IsSpecificAreaAnalysis
    {
        get => _isSpecificAreaAnalysis;
        set
        {
            if (SetProperty(ref _isSpecificAreaAnalysis, value))
            {
                if (value)
                {
                    _isFullImageAnalysis = false;
                    OnPropertyChanged(nameof(IsFullImageAnalysis));
                }
            }
        }
    }

    public bool RequiresZone => SelectedCatalogAnalytic?.Id == "zone_intrusion";
    public bool AllowsOptionalZone => SelectedCatalogAnalytic?.Id is "hand_raise" or "person_presence";
    public bool RequiresLine => SelectedCatalogAnalytic?.Id is "line_crossing" or "person_counting";
    public bool HasEvidenceFeature => SelectedCatalogAnalytic?.Features.Contains(AnalyticFeature.Snapshots) == true;

    public void SetupDynamicParameters(AnalyticDefinition definition)
    {
        DynamicParameters.Clear();
        foreach (var p in definition.Parameters)
        {
            DynamicParameters.Add(new AnalyticParameterItemViewModel(p));
        }

        if (RequiresZone)
        {
            IsFullImageAnalysis = false;
            IsSpecificAreaAnalysis = true;
        }
        else
        {
            IsFullImageAnalysis = true;
            IsSpecificAreaAnalysis = false;
        }

        if (EditorZones.Count > 0 && string.IsNullOrEmpty(SelectedTargetZoneName))
        {
            SelectedTargetZoneName = EditorZones[0].Name;
        }
        if (EditorLines.Count > 0 && string.IsNullOrEmpty(SelectedTargetLineId))
        {
            SelectedTargetLineId = EditorLines[0].Id;
        }

        OnPropertyChanged(nameof(RequiresZone));
        OnPropertyChanged(nameof(AllowsOptionalZone));
        OnPropertyChanged(nameof(RequiresLine));
        OnPropertyChanged(nameof(HasEvidenceFeature));
    }

    public ObservableCollection<string> AiCategoryOptions => AiCategories;
    public bool HasCatalogEntries => AvailableCatalog.Count > 0;
    public bool HasNoCatalogEntries => AvailableCatalog.Count == 0;

    public string? AddAiError
    {
        get => _addAiError;
        set
        {
            if (SetProperty(ref _addAiError, value))
            {
                OnPropertyChanged(nameof(HasAddAiError));
            }
        }
    }
    public bool HasAddAiError => !string.IsNullOrWhiteSpace(_addAiError);

    public bool AiSuccessBannerVisible { get => _aiSuccessBannerVisible; set => SetProperty(ref _aiSuccessBannerVisible, value); }
    public string? AiSuccessMessage { get => _aiSuccessMessage; private set => SetProperty(ref _aiSuccessMessage, value); }

    // Contextual Geometry Creation for AI Wizard
    private bool _isCreatingContextualZone;
    private bool _isCreatingContextualLine;
    private bool _isDrawingRectangle;
    private string _selectedLineDirectionText = "Bidireccional";

    public bool IsDrawingRectangle
    {
        get => _isDrawingRectangle;
        private set => SetProperty(ref _isDrawingRectangle, value);
    }

    public bool IsCreatingContextualZone
    {
        get => _isCreatingContextualZone;
        private set
        {
            if (SetProperty(ref _isCreatingContextualZone, value))
            {
                OnPropertyChanged(nameof(IsCreatingGeometryForAi));
                OnPropertyChanged(nameof(IsNormalMode));
                OnPropertyChanged(nameof(ContextualBreadcrumbText));
            }
        }
    }

    public bool IsCreatingContextualLine
    {
        get => _isCreatingContextualLine;
        private set
        {
            if (SetProperty(ref _isCreatingContextualLine, value))
            {
                OnPropertyChanged(nameof(IsCreatingGeometryForAi));
                OnPropertyChanged(nameof(IsNormalMode));
                OnPropertyChanged(nameof(ContextualBreadcrumbText));
            }
        }
    }

    public bool IsCreatingGeometryForAi => IsCreatingContextualZone || IsCreatingContextualLine;
    public bool IsNormalMode => !IsCreatingGeometryForAi;

    public string ContextualBreadcrumbText
    {
        get
        {
            if (IsCreatingContextualZone) return IsDrawingRectangle ? "Espacios / Nuevo rectángulo" : "Espacios / Nueva zona";
            if (IsCreatingContextualLine) return "Espacios / Nueva línea";
            return _detailTabIndex switch
            {
                0 => "Monitor",
                1 => "Soluciones IA",
                2 => "Espacios y Zonas",
                3 => "Reglas",
                4 => "Eventos",
                5 => "Configuración",
                _ => "Monitor"
            };
        }
    }

    public string SelectedLineDirectionText { get => _selectedLineDirectionText; set => SetProperty(ref _selectedLineDirectionText, value); }

    // AI Wizard Commands
    public IRelayCommand OpenAddAiModalCommand { get; }
    public IRelayCommand CloseAddAiModalCommand { get; }
    public IRelayCommand NextAiWizardStepCommand { get; }
    public IRelayCommand PrevAiWizardStepCommand { get; }
    public IAsyncRelayCommand ConfirmAddAiSolutionCommand { get; }
    public IRelayCommand StartContextualZoneCreationCommand { get; }
    public IRelayCommand StartContextualRectangleCreationCommand { get; }
    public IRelayCommand StartContextualLineCreationCommand { get; }
    public IAsyncRelayCommand SaveContextualZoneAndReturnCommand { get; }
    public IAsyncRelayCommand SaveContextualLineAndReturnCommand { get; }
    public IRelayCommand CancelContextualGeometryAndReturnCommand { get; }
    public IAsyncRelayCommand CreateZoneInlineCommand { get; }
    public IAsyncRelayCommand CreateLineInlineCommand { get; }
    public IRelayCommand GoToRulesCommand { get; }
    public IRelayCommand GoToMonitorCommand { get; }
    public IRelayCommand DismissAiSuccessBannerCommand { get; }

    // Rules Modal Properties & Commands
    public ObservableCollection<AlertRuleItemViewModel> CameraRules { get; }
    public ObservableCollection<string> AvailableRuleEventTypes { get; }
    public ObservableCollection<RuleEventOption> AvailableRuleEventOptions { get; }
    public IReadOnlyList<string> SeverityOptions { get; } = ["Baja", "Media", "Alta", "Crítica"];

    public bool IsRulesModalOpen
    {
        get => _isRulesModalOpen;
        set => SetProperty(ref _isRulesModalOpen, value);
    }

    public bool IsEditingRule
    {
        get => _isEditingRule;
        set
        {
            if (SetProperty(ref _isEditingRule, value))
            {
                OnPropertyChanged(nameof(IsViewingRulesList));
                OnPropertyChanged(nameof(RuleFormTitle));
            }
        }
    }

    public bool IsViewingRulesList => !_isEditingRule;
    public string RuleFormTitle => string.IsNullOrEmpty(_ruleEditId) ? "Nueva Regla de Alerta" : "Editar Regla de Alerta";

    public CameraAnalyticItemViewModel? SelectedRuleAnalytic
    {
        get => _selectedRuleAnalytic;
        set
        {
            if (SetProperty(ref _selectedRuleAnalytic, value))
            {
                UpdateAvailableRuleEventTypes();
                _ = LoadRulesForSelectedAnalyticAsync();
            }
        }
    }

    public bool HasNoCameraRules => CameraRules.Count == 0;
    public bool HasCameraRules => CameraRules.Count > 0;
    public bool HasMultipleAssignedAnalytics => AssignedAnalytics.Count > 1;
    public bool HasSingleAssignedAnalytic => AssignedAnalytics.Count == 1;

    public string? RuleEditId
    {
        get => _ruleEditId;
        set
        {
            if (SetProperty(ref _ruleEditId, value))
            {
                OnPropertyChanged(nameof(RuleFormTitle));
            }
        }
    }

    public string RuleEditName
    {
        get => _ruleEditName;
        set => SetProperty(ref _ruleEditName, value);
    }

    public string? RuleEditEventType
    {
        get => _ruleEditEventType;
        set => SetProperty(ref _ruleEditEventType, value);
    }

    public string RuleEditSeverity
    {
        get => _ruleEditSeverity;
        set => SetProperty(ref _ruleEditSeverity, value);
    }

    public int RuleEditCooldownSeconds
    {
        get => _ruleEditCooldownSeconds;
        set => SetProperty(ref _ruleEditCooldownSeconds, value);
    }

    public bool RuleEditEnabled
    {
        get => _ruleEditEnabled;
        set => SetProperty(ref _ruleEditEnabled, value);
    }

    public string? RuleErrorMessage
    {
        get => _ruleErrorMessage;
        set
        {
            if (SetProperty(ref _ruleErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasRuleError));
            }
        }
    }
    public bool HasRuleError => !string.IsNullOrWhiteSpace(_ruleErrorMessage);

    public IRelayCommand OpenRulesModalCommand { get; }
    public IRelayCommand CloseRulesModalCommand { get; }
    public IRelayCommand StartCreateRuleCommand { get; }
    public IRelayCommand<AlertRuleItemViewModel?> StartEditRuleCommand { get; }
    public IRelayCommand StartCreateRuleFromWorkspaceCommand { get; }
    public IRelayCommand<AlertRuleItemViewModel?> StartEditRuleFromWorkspaceCommand { get; }
    public IRelayCommand CancelEditRuleCommand { get; }
    public IAsyncRelayCommand SaveRuleCommand { get; }
    public IAsyncRelayCommand<AlertRuleItemViewModel?> DeleteRuleCommand { get; }

    public ObservableCollection<AlertRuleItemViewModel> ConfiguredRules { get; }
    public bool HasConfiguredRules => ConfiguredRules.Count > 0;
    public bool HasNoConfiguredRules => ConfiguredRules.Count == 0;

    public ObservableCollection<AnalyticMetricItemViewModel> ConfiguredAnalyticsMetrics { get; }
    public bool HasConfiguredAnalyticsMetrics => ConfiguredAnalyticsMetrics.Count > 0;
    public bool HasNoConfiguredAnalyticsMetrics => ConfiguredAnalyticsMetrics.Count == 0;

    // Zones & Lines
    public bool IsEditingZones
    {
        get => _isEditingZones;
        private set
        {
            if (SetProperty(ref _isEditingZones, value))
            {
                OnPropertyChanged(nameof(ZoneEditorButtonText));
            }
        }
    }

    public string ZoneEditorButtonText => IsEditingZones ? "Salir del editor" : "Editar zonas";
    public string ZoneName { get => _zoneName; set => SetProperty(ref _zoneName, value); }
    public string? ZoneMessage { get => _zoneMessage; private set => SetProperty(ref _zoneMessage, value); }
    public IReadOnlyList<NormalizedZone> EditorZones
    {
        get => _editorZones;
        private set
        {
            if (SetProperty(ref _editorZones, value))
            {
                OnPropertyChanged(nameof(HasZones));
                OnPropertyChanged(nameof(HasNoZones));
            }
        }
    }
    public IReadOnlyList<NormalizedPoint> DraftPoints { get => _draftPoints; private set => SetProperty(ref _draftPoints, value); }
    public NormalizedZone? SelectedZone
    {
        get => _selectedZone;
        set
        {
            if (SetProperty(ref _selectedZone, value) && value is not null)
            {
                ZoneName = value.Name;
            }
        }
    }

    public IReadOnlyList<LineDefinition> EditorLines
    {
        get => _editorLines;
        private set
        {
            if (SetProperty(ref _editorLines, value))
            {
                OnPropertyChanged(nameof(HasLines));
                OnPropertyChanged(nameof(HasNoLines));
            }
        }
    }
    public LineDefinition? SelectedLine { get => _selectedLine; set => SetProperty(ref _selectedLine, value); }
    public string NewLineName { get => _newLineName; set => SetProperty(ref _newLineName, value); }
    public LineDirectionMode SelectedLineDirection { get => _selectedLineDirection; set => SetProperty(ref _selectedLineDirection, value); }
    public string? LineMessage { get => _lineMessage; private set => SetProperty(ref _lineMessage, value); }

    // Empty State Helpers
    public bool HasAssignedAnalytics => AssignedAnalytics.Count > 0;
    public bool HasNoAssignedAnalytics => AssignedAnalytics.Count == 0;
    public bool HasZones => EditorZones.Count > 0;
    public bool HasNoZones => EditorZones.Count == 0;
    public bool HasLines => EditorLines.Count > 0;
    public bool HasNoLines => EditorLines.Count == 0;

    // Config editing
    public string EditName { get => _editName; set => SetProperty(ref _editName, value); }
    public string EditSource { get => _editSource; set => SetProperty(ref _editSource, value); }
    public bool EditEnabled { get => _editEnabled; set => SetProperty(ref _editEnabled, value); }
    public string? ConfigMessage { get => _configMessage; private set => SetProperty(ref _configMessage, value); }
    private bool _isTestingConfigConnection;
    public bool IsTestingConfigConnection { get => _isTestingConfigConnection; private set => SetProperty(ref _isTestingConfigConnection, value); }

    // Runtime Plan
    public string RuntimePlanSummary { get => _runtimePlanSummary; private set => SetProperty(ref _runtimePlanSummary, value); }
    public string RequiredCapabilitiesSummary { get => _requiredCapabilitiesSummary; private set => SetProperty(ref _requiredCapabilitiesSummary, value); }
    public string ModelProviderSummary { get => _modelProviderSummary; private set => SetProperty(ref _modelProviderSummary, value); }

    // Commands
    public IAsyncRelayCommand ToggleCommand { get; }
    public IRelayCommand ToggleZoneEditorCommand { get; }
    public IAsyncRelayCommand CloseZoneCommand { get; }
    public IAsyncRelayCommand UndoZoneCommand { get; }
    public IAsyncRelayCommand DeleteZoneCommand { get; }
    public IRelayCommand NewZoneCommand { get; }

    public IAsyncRelayCommand AddLineCommand { get; }
    public IAsyncRelayCommand DeleteLineCommand { get; }

    public IAsyncRelayCommand AddAnalyticCommand { get; }
    public IAsyncRelayCommand<CameraAnalyticItemViewModel?> RemoveAnalyticCommand { get; }
    public IAsyncRelayCommand RefreshAnalyticsCommand { get; }
    public IAsyncRelayCommand SaveConfigCommand { get; }
    public IAsyncRelayCommand TestConfigConnectionCommand { get; }
    public IAsyncRelayCommand RefreshEventsCommand { get; }
    public IRelayCommand OpenConfigTabCommand { get; }

    public void UpdateFromView(CameraView view)
    {
        Name = view.Name;
        _source = view.Source;
        EditName = view.Name;
        EditSource = view.Source;
        EditEnabled = view.Enabled;
        OnPropertyChanged(nameof(SourceDescription));
        OnPropertyChanged(nameof(SourceType));

        IsRunning = view.Running;
        if (!_isStreaming)
        {
            Status = view.Running
                ? (view.Online ? "En línea" : "Conectando...")
                : "Desconectada";
        }

        if (view.Error is not null)
        {
            Error = view.Error;
        }
        else if (view.Online)
        {
            Error = null;
        }

        if (!_isStreaming || FramesPerSecond == 0)
        {
            FramesPerSecond = view.FramesPerSecond;
        }
    }

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_isStreaming) return;
            _isStreaming = true;
            IsRunning = true;
            Status = "Conectando...";
            Error = null;
            _streamCancellation = new CancellationTokenSource();
            _streamTask = Task.Run(() => StreamLoopAsync(_streamCancellation.Token));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopStreamingAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (!_isStreaming) return;
            _isStreaming = false;
            _streamCancellation?.Cancel();
            if (_streamTask is not null)
            {
                try { await _streamTask; } catch (OperationCanceledException) { }
            }
            if (_subscription is not null)
            {
                try { await _subscription.DisposeAsync(); } catch { }
                _subscription = null;
            }
            _streamCancellation?.Dispose();
            _streamCancellation = null;
            _streamTask = null;
            FramesPerSecond = 0;
            VideoImage = null;
            Status = "Desconectada";
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task ReconnectAsync()
    {
        Error = null;
        Status = "Reconectando...";
        await StopStreamingAsync();
        await StartAsync();
    }

    private async Task TakeSnapshotAsync()
    {
        if (!_isStreaming || _videoImage is null)
        {
            ConfigMessage = "No se puede capturar instantánea: el video no está activo.";
            return;
        }

        try
        {
            var bytes = await _cameraService.GetSnapshotAsync(Id);
            if (bytes is not null && bytes.Length > 0)
            {
                Directory.CreateDirectory(_snapshotDirectory);
                var filePath = Path.Combine(_snapshotDirectory, $"snap_{Id}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.jpg");
                await File.WriteAllBytesAsync(filePath, bytes);
                ConfigMessage = $"Instantánea guardada en {filePath}";
            }
            else
            {
                ConfigMessage = "No se pudo obtener el fotograma actual.";
            }
        }
        catch (Exception ex)
        {
            ConfigMessage = $"Error al capturar instantánea: {ex.Message}";
        }
    }

    private async Task ToggleAsync()
    {
        if (_isToggling) return;
        _isToggling = true;
        try
        {
            if (IsRunning)
            {
                await _cameraService.StopAsync(Id);
                await StopStreamingAsync();
                IsRunning = false;
                Status = "Detenida";
            }
            else
            {
                await _cameraService.StartAsync(Id);
                IsRunning = true;
                await StartAsync();
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            _isToggling = false;
        }
    }

    private async Task StreamLoopAsync(CancellationToken token)
    {
        var frameCount = 0;
        var fpsStopwatch = Stopwatch.StartNew();

        while (!token.IsCancellationRequested)
        {
            try
            {
                var subResult = await _cameraService.SubscribeStreamAsync(Id, fps: 30, quality: 75, token);
                if (subResult.Status == CameraStreamStatus.Success && subResult.Reader is not null)
                {
                    _subscription = subResult.Subscription;
                    DispatchUi(() =>
                    {
                        Status = "En línea";
                        Error = null;
                    });

                    await foreach (var jpeg in subResult.Reader.ReadAllAsync(token))
                    {
                        frameCount++;
                        if (fpsStopwatch.ElapsedMilliseconds >= 500)
                        {
                            var currentFps = frameCount * 1000.0 / fpsStopwatch.ElapsedMilliseconds;
                            frameCount = 0;
                            fpsStopwatch.Restart();
                            _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, () => FramesPerSecond = Math.Round(currentFps, 1));
                        }

                        if (Interlocked.CompareExchange(ref _isRenderingFrame, 1, 0) == 0)
                        {
                            try
                            {
                                var bitmap = DecodeJpeg(jpeg);
                                _ = _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                                {
                                    try
                                    {
                                        VideoImage = bitmap;
                                        FrameWidth = bitmap.PixelWidth;
                                        FrameHeight = bitmap.PixelHeight;
                                        Status = "En línea";
                                        Error = null;
                                        _lastFrameTime = DateTimeOffset.UtcNow;
                                    }
                                    finally
                                    {
                                        Interlocked.Exchange(ref _isRenderingFrame, 0);
                                    }
                                });
                            }
                            catch
                            {
                                Interlocked.Exchange(ref _isRenderingFrame, 0);
                            }
                        }
                    }
                }
                else
                {
                    DispatchUi(() =>
                    {
                        Status = subResult.Status switch
                        {
                            CameraStreamStatus.Offline => "Cámara detenida / fuera de línea",
                            CameraStreamStatus.LimitReached => "Límite de clientes alcanzado",
                            CameraStreamStatus.NotFound => "Cámara no encontrada",
                            _ => "Esperando señal de video..."
                        };
                        if (subResult.Error is not null) Error = subResult.Error;
                        OnPropertyChanged(nameof(CanonicalStateText));
                        OnPropertyChanged(nameof(CanonicalStateIcon));
                        OnPropertyChanged(nameof(CanonicalStateBrush));
                        OnPropertyChanged(nameof(ContextualDiagnosticReason));
                    });
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                DispatchUi(() =>
                {
                    Status = "Sin señal / reconectando";
                    Error = ex.Message;
                    OnPropertyChanged(nameof(CanonicalStateText));
                    OnPropertyChanged(nameof(CanonicalStateIcon));
                    OnPropertyChanged(nameof(CanonicalStateBrush));
                    OnPropertyChanged(nameof(ContextualDiagnosticReason));
                    OnPropertyChanged(nameof(LastFrameAgoText));
                    OnPropertyChanged(nameof(IsReconnecting));
                });
            }
            finally
            {
                if (_subscription is not null)
                {
                    try { await _subscription.DisposeAsync(); } catch { }
                    _subscription = null;
                }
            }

            if (!token.IsCancellationRequested)
            {
                await Task.Delay(1000, token);
            }
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

    public async Task InitializeZonesAsync()
    {
        if (_zonesInitialized) return;
        try
        {
            var saved = await _cameraService.GetZonesAsync(Id);
            var lines = _lineStore is not null ? await _lineStore.ListByCameraAsync(Id) : null;
            await DispatchAsync(() =>
            {
                if (saved is not null)
                {
                    EditorZones = saved;
                }
                if (lines is not null)
                {
                    EditorLines = lines;
                }
                _zonesInitialized = true;
            });
        }
        catch (Exception ex)
        {
            await DispatchAsync(() =>
            {
                ZoneMessage = $"Error al cargar zonas/líneas: {ex.Message}";
            });
        }
    }

    public async Task LoadAssignedAnalyticsAsync()
    {
        if (_analyticService is null) return;
        try
        {
            var instances = await _analyticService.ListByCameraAsync(Id) ?? [];
            var catalog = _analyticCatalog?.GetAll() ?? [];

            await DispatchAsync(() =>
            {
                AvailableCatalog.Clear();
                foreach (var item in catalog)
                {
                    AvailableCatalog.Add(item);
                }
                FilterCatalog();
                if (AvailableCatalog.Count > 0 && SelectedCatalogAnalytic is null)
                {
                    SelectedCatalogAnalytic = AvailableCatalog[0];
                }

                AssignedAnalytics.Clear();
                foreach (var inst in instances)
                {
                    var def = catalog.FirstOrDefault(d => d.Id == inst.AnalyticTypeId);
                    AssignedAnalytics.Add(new CameraAnalyticItemViewModel(
                        inst,
                        def,
                        onToggle: async (instanceId, enabled) =>
                        {
                            await _analyticService.UpdateAsync(Id, instanceId, new CameraAnalyticWriteRequest(Enabled: enabled));
                            await DispatchAsync(() =>
                            {
                                UpdateRuntimePlan();
                                OnPropertyChanged(nameof(ActiveAnalyticsCount));
                            });
                        },
                        onRemove: async (instanceId) =>
                        {
                            await _analyticService.DeleteAsync(Id, instanceId);
                            await LoadAssignedAnalyticsAsync();
                        }));
                }
                UpdateRuntimePlan();
                OnPropertyChanged(nameof(ActiveAnalyticsCount));
                OnPropertyChanged(nameof(HasAssignedAnalytics));
                OnPropertyChanged(nameof(HasNoAssignedAnalytics));
                OnPropertyChanged(nameof(HasConfiguredAnalytics));
                OnPropertyChanged(nameof(HasNoConfiguredAnalytics));
                OnPropertyChanged(nameof(HasMultipleAssignedAnalytics));
                OnPropertyChanged(nameof(HasSingleAssignedAnalytic));
                OnPropertyChanged(nameof(ActivitySummaryText));
                OnPropertyChanged(nameof(OperationalResultsSummary));
                OnPropertyChanged(nameof(HasCatalogEntries));
                OnPropertyChanged(nameof(HasNoCatalogEntries));

                if (SelectedRuleAnalytic is null || !AssignedAnalytics.Any(a => a.InstanceId == SelectedRuleAnalytic.InstanceId))
                {
                    SelectedRuleAnalytic = AssignedAnalytics.FirstOrDefault();
                }

                UpdateConfiguredAnalyticsMetrics();
                _ = LoadConfiguredRulesAsync();
            });
        }
        catch
        {
        }
    }

    public void OpenRulesModal()
    {
        if (AssignedAnalytics.Count == 0) return;
        AiSuccessBannerVisible = false;
        IsRulesTabSelected = true;
        IsRulesModalOpen = true;
        IsEditingRule = false;
        RuleErrorMessage = null;

        if (SelectedRuleAnalytic == null || !AssignedAnalytics.Any(a => a.InstanceId == SelectedRuleAnalytic.InstanceId))
        {
            SelectedRuleAnalytic = AssignedAnalytics.FirstOrDefault();
        }
        else
        {
            UpdateAvailableRuleEventTypes();
            _ = LoadRulesForSelectedAnalyticAsync();
        }
    }

    public void CloseRulesModal()
    {
        IsRulesModalOpen = false;
        IsEditingRule = false;
        RuleErrorMessage = null;
    }

    public void UpdateAvailableRuleEventTypes()
    {
        AvailableRuleEventTypes.Clear();
        AvailableRuleEventOptions.Clear();
        if (SelectedRuleAnalytic is null) return;

        var def = AvailableCatalog.FirstOrDefault(d => d.Id == SelectedRuleAnalytic.AnalyticTypeId)
            ?? _analyticCatalog?.GetById(SelectedRuleAnalytic.AnalyticTypeId);

        if (def != null && def.ProducedEventTypes.Count > 0)
        {
            foreach (var evt in def.ProducedEventTypes)
            {
                AvailableRuleEventTypes.Add(evt);
                AvailableRuleEventOptions.Add(new RuleEventOption(evt, AlertRuleItemViewModel.TranslateEventType(evt)));
            }
        }
        else
        {
            var defaults = SelectedRuleAnalytic.AnalyticTypeId switch
            {
                "hand_raise" => new[] { "hand_raised", "hand_lowered" },
                "person_presence" => new[] { "person_presence_started", "person_presence_ended" },
                "zone_intrusion" => new[] { "zone_intrusion_started", "zone_intrusion_ended" },
                "line_crossing" => new[] { "line_crossed" },
                "person_counting" => new[] { "person_count_updated", "occupancy_threshold_reached" },
                _ => new[] { SelectedRuleAnalytic.AnalyticTypeId }
            };
            foreach (var d in defaults)
            {
                AvailableRuleEventTypes.Add(d);
                AvailableRuleEventOptions.Add(new RuleEventOption(d, AlertRuleItemViewModel.TranslateEventType(d)));
            }
        }

        if (string.IsNullOrEmpty(RuleEditEventType) || !AvailableRuleEventTypes.Contains(RuleEditEventType))
        {
            RuleEditEventType = AvailableRuleEventTypes.FirstOrDefault();
        }
    }

    public async Task LoadRulesForSelectedAnalyticAsync()
    {
        CameraRules.Clear();
        if (SelectedRuleAnalytic is null)
        {
            OnPropertyChanged(nameof(HasNoCameraRules));
            OnPropertyChanged(nameof(HasCameraRules));
            return;
        }

        if (_ruleStore is not null)
        {
            try
            {
                var rules = await _ruleStore.ListByInstanceAsync(Id, SelectedRuleAnalytic.InstanceId);
                foreach (var rule in rules)
                {
                    CameraRules.Add(new AlertRuleItemViewModel(
                        rule,
                        onEdit: r => { StartEditRule(r); return Task.CompletedTask; },
                        onDelete: r => DeleteRuleAsync(r)));
                }
            }
            catch (Exception ex)
            {
                RuleErrorMessage = $"Error al cargar reglas: {ex.Message}";
            }
        }

        OnPropertyChanged(nameof(HasNoCameraRules));
        OnPropertyChanged(nameof(HasCameraRules));
    }

    public void StartCreateRule()
    {
        RuleEditId = null;
        RuleEditName = $"Alerta {SelectedRuleAnalytic?.DisplayName ?? "IA"}";
        UpdateAvailableRuleEventTypes();
        RuleEditEventType = AvailableRuleEventTypes.FirstOrDefault();
        RuleEditSeverity = "Alta";
        RuleEditCooldownSeconds = 5;
        RuleEditEnabled = true;
        RuleErrorMessage = null;
        IsEditingRule = true;
    }

    public void StartEditRule(AlertRuleItemViewModel? item)
    {
        if (item is null) return;
        RuleEditId = item.Id;
        RuleEditName = item.Name;
        UpdateAvailableRuleEventTypes();
        RuleEditEventType = item.Rule.EventTypes?.FirstOrDefault() ?? AvailableRuleEventTypes.FirstOrDefault();
        RuleEditSeverity = item.SeverityText;
        RuleEditCooldownSeconds = item.CooldownSeconds;
        RuleEditEnabled = item.Enabled;
        RuleErrorMessage = null;
        IsEditingRule = true;
    }

    public void CancelEditRule()
    {
        IsEditingRule = false;
        RuleErrorMessage = null;
    }

    public async Task SaveRuleAsync()
    {
        RuleErrorMessage = null;

        if (SelectedRuleAnalytic is null)
        {
            RuleErrorMessage = "Debe seleccionar una solución IA para asociar la regla.";
            return;
        }

        if (string.IsNullOrWhiteSpace(RuleEditName))
        {
            RuleErrorMessage = "El nombre de la regla es obligatorio.";
            return;
        }

        if (string.IsNullOrWhiteSpace(RuleEditEventType))
        {
            RuleErrorMessage = "Debe seleccionar un evento disparador válido.";
            return;
        }

        if (RuleEditCooldownSeconds < 0)
        {
            RuleErrorMessage = "El tiempo de espera entre alertas no puede ser negativo.";
            return;
        }

        var severity = RuleEditSeverity switch
        {
            "Baja" => AlertSeverity.Low,
            "Media" => AlertSeverity.Medium,
            "Alta" => AlertSeverity.High,
            "Crítica" => AlertSeverity.Critical,
            _ => AlertSeverity.Medium
        };

        var ruleId = RuleEditId ?? Guid.NewGuid().ToString("N")[..8];
        var rule = new AlertRule(
            Id: ruleId,
            CameraId: Id,
            AnalyticInstanceId: SelectedRuleAnalytic.InstanceId,
            Name: RuleEditName.Trim(),
            Enabled: RuleEditEnabled,
            EventTypes: [RuleEditEventType],
            Conditions: null,
            Severity: severity,
            TitleTemplate: "{event_type}",
            DescriptionTemplate: $"{SelectedRuleAnalytic.DisplayName} en {Name}",
            CooldownMs: (long)RuleEditCooldownSeconds * 1000,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        try
        {
            if (_ruleStore is not null)
            {
                await _ruleStore.SaveAsync(rule);
                await LoadRulesForSelectedAnalyticAsync();
            }
            else
            {
                // In-memory fallback
                var existing = CameraRules.FirstOrDefault(r => r.Id == ruleId);
                if (existing != null) CameraRules.Remove(existing);
                CameraRules.Add(new AlertRuleItemViewModel(
                    rule,
                    onEdit: r => { StartEditRule(r); return Task.CompletedTask; },
                    onDelete: r => DeleteRuleAsync(r),
                    analyticDisplayName: SelectedRuleAnalytic.DisplayName));
                OnPropertyChanged(nameof(HasNoCameraRules));
                OnPropertyChanged(nameof(HasCameraRules));
            }

            await LoadConfiguredRulesAsync();
            IsEditingRule = false;
        }
        catch (Exception ex)
        {
            RuleErrorMessage = $"Error al guardar la regla: {ex.Message}";
        }
    }

    public async Task DeleteRuleAsync(AlertRuleItemViewModel? item)
    {
        if (item is null) return;
        try
        {
            if (_ruleStore is not null)
            {
                await _ruleStore.DeleteAsync(Id, item.AnalyticInstanceId, item.Id);
                await LoadRulesForSelectedAnalyticAsync();
            }
            else
            {
                CameraRules.Remove(item);
                OnPropertyChanged(nameof(HasNoCameraRules));
                OnPropertyChanged(nameof(HasCameraRules));
            }

            await LoadConfiguredRulesAsync();
        }
        catch (Exception ex)
        {
            RuleErrorMessage = $"Error al eliminar regla: {ex.Message}";
        }
    }

    public async Task LoadConfiguredRulesAsync()
    {
        if (_ruleStore is null)
        {
            await DispatchAsync(() =>
            {
                ConfiguredRules.Clear();
                foreach (var r in CameraRules)
                {
                    ConfiguredRules.Add(r);
                }
                OnPropertyChanged(nameof(HasConfiguredRules));
                OnPropertyChanged(nameof(HasNoConfiguredRules));
            });
            return;
        }

        try
        {
            var rules = await _ruleStore.ListByCameraAsync(Id);
            await DispatchAsync(() =>
            {
                ConfiguredRules.Clear();
                foreach (var rule in rules)
                {
                    var analytic = AssignedAnalytics.FirstOrDefault(a => a.InstanceId == rule.AnalyticInstanceId);
                    var analyticName = analytic?.DisplayName ?? "Solución IA";
                    var item = new AlertRuleItemViewModel(
                        rule,
                        onEdit: r => { StartEditRuleFromWorkspace(r); return Task.CompletedTask; },
                        onDelete: async r => { await DeleteRuleAsync(r); },
                        analyticDisplayName: analyticName);
                    ConfiguredRules.Add(item);
                }
                OnPropertyChanged(nameof(HasConfiguredRules));
                OnPropertyChanged(nameof(HasNoConfiguredRules));
            });
        }
        catch (Exception ex)
        {
            await DispatchAsync(() =>
            {
                RuleErrorMessage = $"Error al cargar reglas: {ex.Message}";
            });
        }
    }

    public void StartCreateRuleFromWorkspace()
    {
        if (AssignedAnalytics.Count == 0) return;
        if (SelectedRuleAnalytic == null || !AssignedAnalytics.Any(a => a.InstanceId == SelectedRuleAnalytic.InstanceId))
        {
            SelectedRuleAnalytic = AssignedAnalytics.FirstOrDefault();
        }
        IsRulesModalOpen = true;
        StartCreateRule();
    }

    public void StartEditRuleFromWorkspace(AlertRuleItemViewModel? item)
    {
        if (item is null) return;
        var matchingAnalytic = AssignedAnalytics.FirstOrDefault(a => a.InstanceId == item.AnalyticInstanceId);
        if (matchingAnalytic != null)
        {
            SelectedRuleAnalytic = matchingAnalytic;
        }
        else if (SelectedRuleAnalytic == null)
        {
            SelectedRuleAnalytic = AssignedAnalytics.FirstOrDefault();
        }
        IsRulesModalOpen = true;
        StartEditRule(item);
    }

    public void UpdateConfiguredAnalyticsMetrics()
    {
        ConfiguredAnalyticsMetrics.Clear();
        foreach (var assigned in AssignedAnalytics)
        {
            int count = assigned.AnalyticTypeId switch
            {
                "hand_raise" => CameraEvents.Count(e => e.RawEventType is "hand_raised" or "hand_lowered"),
                "person_presence" => CameraEvents.Count(e => e.RawEventType.Contains("presence", StringComparison.OrdinalIgnoreCase)),
                "zone_intrusion" => CameraEvents.Count(e => e.RawEventType.Contains("intrusion", StringComparison.OrdinalIgnoreCase)),
                "line_crossing" => CameraEvents.Count(e => e.RawEventType.Contains("line", StringComparison.OrdinalIgnoreCase)),
                "person_counting" => CameraEvents.Count(e => e.RawEventType.Contains("count", StringComparison.OrdinalIgnoreCase) || e.RawEventType.Contains("occupancy", StringComparison.OrdinalIgnoreCase)),
                _ => CameraEvents.Count(e => e.RawEventType.Contains(assigned.AnalyticTypeId, StringComparison.OrdinalIgnoreCase))
            };

            var lastEvent = CameraEvents.FirstOrDefault(e => assigned.AnalyticTypeId switch
            {
                "hand_raise" => e.RawEventType is "hand_raised" or "hand_lowered",
                "person_presence" => e.RawEventType.Contains("presence", StringComparison.OrdinalIgnoreCase),
                "zone_intrusion" => e.RawEventType.Contains("intrusion", StringComparison.OrdinalIgnoreCase),
                "line_crossing" => e.RawEventType.Contains("line", StringComparison.OrdinalIgnoreCase),
                "person_counting" => e.RawEventType.Contains("count", StringComparison.OrdinalIgnoreCase) || e.RawEventType.Contains("occupancy", StringComparison.OrdinalIgnoreCase),
                _ => e.RawEventType.Contains(assigned.AnalyticTypeId, StringComparison.OrdinalIgnoreCase)
            });

            var lastText = lastEvent != null ? $"{lastEvent.Title} ({lastEvent.TimestampText})" : "Sin eventos registrados";

            ConfiguredAnalyticsMetrics.Add(new AnalyticMetricItemViewModel(
                instanceId: assigned.InstanceId,
                analyticTypeId: assigned.AnalyticTypeId,
                displayName: assigned.DisplayName,
                icon: assigned.Icon,
                enabled: assigned.Enabled,
                statusText: assigned.Enabled ? "Activa" : "Pausada",
                eventsCount: count,
                lastEventText: lastText));
        }

        OnPropertyChanged(nameof(HasConfiguredAnalyticsMetrics));
        OnPropertyChanged(nameof(HasNoConfiguredAnalyticsMetrics));
    }

    public async Task RemoveAnalyticAsync(CameraAnalyticItemViewModel? item)
    {
        if (item is null || _analyticService is null) return;
        try
        {
            await _analyticService.DeleteAsync(Id, item.InstanceId);
            await LoadAssignedAnalyticsAsync();
        }
        catch (Exception ex)
        {
            await DispatchAsync(() =>
            {
                AddAiError = $"Error al eliminar solución IA: {ex.Message}";
            });
        }
    }

    private void UpdateRuntimePlan()
    {
        var active = AssignedAnalytics.Where(a => a.Enabled).ToList();
        if (active.Count == 0)
        {
            RuntimePlanSummary = "Sin soluciones IA activas en esta cámara";
            RequiredCapabilitiesSummary = "Ninguna";
            ModelProviderSummary = "Inactivo";
            return;
        }

        var caps = active.Select(a => a.CapabilitiesSummary).Distinct();
        RequiredCapabilitiesSummary = string.Join(", ", caps);
        RuntimePlanSummary = $"{active.Count} solución(es) IA activa(s): {string.Join(", ", active.Select(a => a.DisplayName))}";
        ModelProviderSummary = "Ultralytics YOLOv8 Pose / Object Detection (DirectML / CPU Fallback)";
    }

    public void FilterCatalog()
    {
        FilteredCatalog.Clear();
        var selected = SelectedAiCategory;
        foreach (var item in AvailableCatalog)
        {
            if (selected == "Todas" ||
                (selected == "General" && item.Category == AnalyticCategory.General) ||
                (selected == "Seguridad" && (item.Category == AnalyticCategory.Security || item.Category == AnalyticCategory.Safety)) ||
                (selected == "Operaciones" && item.Category == AnalyticCategory.Operations) ||
                (selected == "Logística" && item.Category == AnalyticCategory.Logistics))
            {
                FilteredCatalog.Add(item);
            }
        }
        if (FilteredCatalog.Count > 0 && (SelectedCatalogAnalytic is null || !FilteredCatalog.Contains(SelectedCatalogAnalytic)))
        {
            SelectedCatalogAnalytic = FilteredCatalog[0];
        }
    }

    public void OpenAddAiModal()
    {
        AddAiError = null;
        _ = InitializeZonesAsync();
        _ = LoadAssignedAnalyticsAsync();
        SelectedAiCategory = "Todas";
        FilterCatalog();
        AiWizardStep = 1;
        ShowAdvancedAiParams = false;
        if (AvailableCatalog.Count > 0 && SelectedCatalogAnalytic is null)
        {
            SelectedCatalogAnalytic = AvailableCatalog[0];
        }
        if (SelectedCatalogAnalytic != null)
        {
            SetupDynamicParameters(SelectedCatalogAnalytic);
        }
        IsAddAiModalOpen = true;
    }

    public void CloseAddAiModal()
    {
        IsAddAiModalOpen = false;
        AddAiError = null;
    }

    public void NextAiWizardStep()
    {
        if (AiWizardStep == 1 && SelectedCatalogAnalytic is null && FilteredCatalog.Count > 0)
        {
            SelectedCatalogAnalytic = FilteredCatalog[0];
        }

        if (AiWizardStep < 4)
        {
            AiWizardStep++;
        }
    }

    public void PrevAiWizardStep()
    {
        if (AiWizardStep > 1)
        {
            AiWizardStep--;
        }
    }

    public async Task ConfirmAddAiSolutionAsync()
    {
        AddAiError = null;
        if (_analyticService is null || SelectedCatalogAnalytic is null) return;
        try
        {
            var config = new Dictionary<string, object?>();
            foreach (var param in DynamicParameters)
            {
                config[param.Key] = param.GetTypedValue();
            }

            List<string>? assignedZones = null;
            if (RequiresZone)
            {
                if (string.IsNullOrWhiteSpace(SelectedTargetZoneName))
                {
                    AddAiError = "Debe seleccionar o dibujar una zona para esta solución IA.";
                    return;
                }
                assignedZones = [SelectedTargetZoneName];
            }
            else if (AllowsOptionalZone)
            {
                if (IsSpecificAreaAnalysis)
                {
                    if (string.IsNullOrWhiteSpace(SelectedTargetZoneName))
                    {
                        AddAiError = "Debe seleccionar o dibujar una zona o cambiar a 'Toda la imagen'.";
                        return;
                    }
                    assignedZones = [SelectedTargetZoneName];
                }
                else
                {
                    assignedZones = null;
                }
            }

            List<string>? assignedLines = null;
            if (RequiresLine)
            {
                if (string.IsNullOrWhiteSpace(SelectedTargetLineId))
                {
                    AddAiError = "Debe seleccionar o crear una línea virtual para esta solución IA.";
                    return;
                }
                assignedLines = [SelectedTargetLineId];
            }

            var request = new CameraAnalyticWriteRequest(
                Id: Guid.NewGuid().ToString("N")[..8],
                AnalyticTypeId: SelectedCatalogAnalytic.Id,
                Name: string.IsNullOrWhiteSpace(AiCustomName) ? SelectedCatalogAnalytic.DisplayName : AiCustomName.Trim(),
                Enabled: true,
                Configuration: config,
                AssignedZoneIds: assignedZones,
                AssignedLineIds: assignedLines);

            await _analyticService.CreateAsync(Id, request);
            await LoadAssignedAnalyticsAsync();

            IsAddAiModalOpen = false;
            AiSuccessMessage = $"Solución '{request.Name}' activada correctamente.";
            AiSuccessBannerVisible = true;
        }
        catch (Exception ex)
        {
            AddAiError = $"Error al activar solución IA: {ex.Message}";
            ConfigMessage = AddAiError;
        }
    }

    private bool _wasAddingAi;

    public void StartContextualZoneCreation()
    {
        _wasAddingAi = true;
        IsAddAiModalOpen = false;
        IsCreatingContextualZone = true;
        IsCreatingContextualLine = false;
        IsEditingZones = true;
        IsDrawingRectangle = false;
        StartNewZone();
        ZoneName = $"Zona {EditorZones.Count + 1}";
        ZoneMessage = "Dibuje la zona sobre el video haciendo clic en cada vértice deseado.";
    }

    public void StartContextualRectangleCreation()
    {
        _wasAddingAi = true;
        IsAddAiModalOpen = false;
        IsCreatingContextualZone = true;
        IsCreatingContextualLine = false;
        IsEditingZones = true;
        IsDrawingRectangle = true;
        StartNewZone();
        ZoneName = $"Zona {EditorZones.Count + 1}";
        ZoneMessage = "Haga clic en la primera esquina del rectángulo sobre el video.";
    }

    public void StartDirectZoneCreation()
    {
        _wasAddingAi = false;
        IsAddAiModalOpen = false;
        IsCreatingContextualZone = true;
        IsCreatingContextualLine = false;
        IsEditingZones = true;
        IsDrawingRectangle = false;
        StartNewZone();
        ZoneName = $"Zona {EditorZones.Count + 1}";
        ZoneMessage = "Dibuje la zona sobre el video haciendo clic en cada vértice deseado.";
    }

    public void StartDirectRectangleCreation()
    {
        _wasAddingAi = false;
        IsAddAiModalOpen = false;
        IsCreatingContextualZone = true;
        IsCreatingContextualLine = false;
        IsEditingZones = true;
        IsDrawingRectangle = true;
        StartNewZone();
        ZoneName = $"Zona {EditorZones.Count + 1}";
        ZoneMessage = "Haga clic en la primera esquina del rectángulo sobre el video.";
    }

    public async Task SaveContextualZoneAndReturnAsync()
    {
        if (DraftPoints.Count < 3)
        {
            ZoneMessage = IsDrawingRectangle
                ? "Debe hacer clic en dos esquinas sobre el video para definir un rectángulo."
                : "Debe añadir al menos 3 puntos haciendo clic sobre la imagen para definir una zona válida.";
            return;
        }

        var finalName = string.IsNullOrWhiteSpace(ZoneName) ? $"Zona {EditorZones.Count + 1}" : ZoneName.Trim();
        try
        {
            var candidate = new NormalizedZone(finalName, DraftPoints.ToArray());
            var zones = EditorZones.Append(candidate).ToArray();
            ZoneEditorLogic.Validate(zones);
            _zoneHistory.Push(CloneZones(EditorZones));
            EditorZones = CloneZones(zones);
            DraftPoints = [];
            SelectedZone = candidate;
            await SaveZonesAsync();

            SelectedTargetZoneName = candidate.Name;
            IsCreatingContextualZone = false;
            IsEditingZones = false;
            IsDrawingRectangle = false;
            if (_wasAddingAi)
            {
                IsAddAiModalOpen = true;
                AiWizardStep = 2;
            }
        }
        catch (Exception ex)
        {
            ZoneMessage = $"Error al validar zona: {ex.Message}";
        }
    }

    public void StartContextualLineCreation()
    {
        _wasAddingAi = true;
        IsAddAiModalOpen = false;
        IsCreatingContextualZone = false;
        IsCreatingContextualLine = true;
        NewLineName = $"Línea {EditorLines.Count + 1}";
        SelectedLineDirectionText = "Bidireccional";
        LineMessage = "Configure el nombre y la dirección del tripwire a continuación.";
    }

    public void StartDirectLineCreation()
    {
        _wasAddingAi = false;
        IsAddAiModalOpen = false;
        IsCreatingContextualZone = false;
        IsCreatingContextualLine = true;
        NewLineName = $"Línea {EditorLines.Count + 1}";
        SelectedLineDirectionText = "Bidireccional";
        LineMessage = "Configure el nombre y la dirección del tripwire a continuación.";
    }

    public async Task SaveContextualLineAndReturnAsync()
    {
        if (_lineStore is null) return;
        var name = string.IsNullOrWhiteSpace(NewLineName) ? $"Línea {EditorLines.Count + 1}" : NewLineName.Trim();
        var dirMode = SelectedLineDirectionText switch
        {
            "A → B" => LineDirectionMode.AToB,
            "B → A" => LineDirectionMode.BToA,
            _ => LineDirectionMode.Bidirectional
        };

        try
        {
            var line = new LineDefinition(
                Id: Guid.NewGuid().ToString("N")[..8],
                CameraId: Id,
                Name: name,
                PointA: new Point2D(0.2, 0.5),
                PointB: new Point2D(0.8, 0.5),
                DirectionMode: dirMode,
                Enabled: true);

            await _lineStore.SaveAsync(Id, line);
            EditorLines = await _lineStore.ListByCameraAsync(Id);
            SelectedLine = line;
            SelectedTargetLineId = line.Id;

            IsCreatingContextualLine = false;
            if (_wasAddingAi)
            {
                IsAddAiModalOpen = true;
                AiWizardStep = 2;
            }
        }
        catch (Exception ex)
        {
            LineMessage = $"Error al crear línea: {ex.Message}";
        }
    }

    public void CancelContextualGeometryAndReturn()
    {
        DraftPoints = [];
        IsCreatingContextualZone = false;
        IsCreatingContextualLine = false;
        IsEditingZones = false;
        IsDrawingRectangle = false;
        if (_wasAddingAi)
        {
            IsAddAiModalOpen = true;
            AiWizardStep = 2;
        }
    }

    public void SelectCatalogAnalyticAndNext(object? parameter)
    {
        AnalyticDefinition? definition = null;
        if (parameter is AnalyticDefinition def)
        {
            definition = def;
        }
        else if (parameter is string typeId)
        {
            var normalizedId = typeId switch
            {
                "person_detect" => "person_presence",
                "person_count" => "person_counting",
                _ => typeId
            };

            definition = AvailableCatalog.FirstOrDefault(d => d.Id == typeId)
                ?? _analyticCatalog?.GetById(typeId)
                ?? AvailableCatalog.FirstOrDefault(d => d.Id == normalizedId)
                ?? _analyticCatalog?.GetById(normalizedId)
                ?? _analyticCatalog?.GetAll().FirstOrDefault(d => d.Id == typeId || d.Id == normalizedId);
        }

        if (definition is null && AvailableCatalog.Count > 0)
        {
            definition = AvailableCatalog[0];
        }

        if (definition is not null)
        {
            SelectedCatalogAnalytic = definition;
            AiCustomName = definition.DisplayName;
            SetupDynamicParameters(definition);
        }
        AiWizardStep = 2;
    }

    public async Task DeleteZoneItemAsync(NormalizedZone? zone)
    {
        if (zone is null) return;
        SelectedZone = zone;
        await DeleteZoneAsync();
    }

    public async Task DeleteLineItemAsync(LineDefinition? line)
    {
        if (line is null) return;
        SelectedLine = line;
        await DeleteLineAsync();
    }

    public async Task CreateZoneInlineAsync()
    {
        StartContextualZoneCreation();
        await Task.CompletedTask;
    }

    public async Task CreateLineInlineAsync()
    {
        StartContextualLineCreation();
        await Task.CompletedTask;
    }

    private async Task AddAnalyticAsync()
    {
        if (_analyticService is null || SelectedCatalogAnalytic is null) return;
        try
        {
            var request = new CameraAnalyticWriteRequest(
                Id: Guid.NewGuid().ToString("N")[..8],
                AnalyticTypeId: SelectedCatalogAnalytic.Id,
                Name: SelectedCatalogAnalytic.DisplayName,
                Enabled: true);

            await _analyticService.CreateAsync(Id, request);
            await LoadAssignedAnalyticsAsync();
        }
        catch (Exception ex)
        {
            ConfigMessage = $"Error al añadir analítica: {ex.Message}";
        }
    }

    private async Task AddLineAsync()
    {
        if (_lineStore is null || string.IsNullOrWhiteSpace(NewLineName)) return;
        try
        {
            var line = new LineDefinition(
                Id: Guid.NewGuid().ToString("N")[..8],
                CameraId: Id,
                Name: NewLineName.Trim(),
                PointA: new Point2D(0.2, 0.5),
                PointB: new Point2D(0.8, 0.5),
                DirectionMode: SelectedLineDirection,
                Enabled: true);

            await _lineStore.SaveAsync(Id, line);
            EditorLines = await _lineStore.ListByCameraAsync(Id);
            LineMessage = $"Línea '{line.Name}' creada.";
        }
        catch (Exception ex)
        {
            LineMessage = $"Error: {ex.Message}";
        }
    }

    private async Task DeleteLineAsync()
    {
        if (_lineStore is null || SelectedLine is null) return;
        try
        {
            await _lineStore.DeleteAsync(Id, SelectedLine.Id);
            EditorLines = await _lineStore.ListByCameraAsync(Id);
            SelectedLine = null;
            LineMessage = "Línea eliminada.";
        }
        catch (Exception ex)
        {
            LineMessage = $"Error al eliminar: {ex.Message}";
        }
    }

    private void StartEventBusSubscription()
    {
        if (_eventBus is null) return;
        try
        {
            var sub = _eventBus.Subscribe();
            _eventBusSubscription = sub;
            _eventBusCancellation = new CancellationTokenSource();
            var token = _eventBusCancellation.Token;

            _eventBusTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var handEvent in sub.Reader.ReadAllAsync(token))
                    {
                        if (token.IsCancellationRequested) break;
                        if (!string.Equals(handEvent.CameraId, _id, StringComparison.OrdinalIgnoreCase)) continue;

                        var eventItem = EventItemViewModel.Create(handEvent, _snapshotDirectory);
                        DispatchUi(async () =>
                        {
                            if (!CameraEvents.Any(e => e.Id == eventItem.Id))
                            {
                                CameraEvents.Insert(0, eventItem);
                                while (CameraEvents.Count > 50) CameraEvents.RemoveAt(CameraEvents.Count - 1);
                                OnPropertyChanged(nameof(HasEvents));
                                OnPropertyChanged(nameof(HasNoEvents));
                                OnPropertyChanged(nameof(LastActivityText));
                                OnPropertyChanged(nameof(TotalDetectionsCount));
                                OnPropertyChanged(nameof(HandRaiseEventsCount));
                                OnPropertyChanged(nameof(PersonPresenceEventsCount));
                                OnPropertyChanged(nameof(ZoneIntrusionEventsCount));
                                OnPropertyChanged(nameof(LineCrossingEventsCount));
                                OnPropertyChanged(nameof(PersonCountingEventsCount));
                                UpdateConfiguredAnalyticsMetrics();
                            }

                            if (_alertStore is not null)
                            {
                                try
                                {
                                    var alertResults = await _alertStore.QueryAsync(new AlertQuery(CameraId: _id, Limit: 20));
                                    foreach (var alert in alertResults)
                                    {
                                        if (!CameraAlerts.Any(a => a.Id == alert.Id))
                                        {
                                            CameraAlerts.Insert(0, new AlertItemViewModel(
                                                alert,
                                                onAcknowledge: async alertId =>
                                                {
                                                    await _alertStore.AcknowledgeAsync(alertId, DateTimeOffset.UtcNow);
                                                    await RefreshEventsAsync();
                                                },
                                                onResolve: async alertId =>
                                                {
                                                    await _alertStore.ResolveAsync(alertId, DateTimeOffset.UtcNow);
                                                    await RefreshEventsAsync();
                                                }));
                                        }
                                    }
                                    while (CameraAlerts.Count > 50) CameraAlerts.RemoveAt(CameraAlerts.Count - 1);
                                    OnPropertyChanged(nameof(HasCameraAlerts));
                                    OnPropertyChanged(nameof(HasNoCameraAlerts));
                                    OnPropertyChanged(nameof(OpenAlertsCount));
                                    OnPropertyChanged(nameof(CriticalAlertsCount));
                                    OnPropertyChanged(nameof(LastAlertText));
                                }
                                catch { }
                            }
                        });
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            }, token);
        }
        catch { }
    }

    private CancellationTokenSource? _eventsCancellation;
    public async Task RefreshEventsAsync()
    {
        _eventsCancellation?.Cancel();
        var cts = new CancellationTokenSource();
        _eventsCancellation = cts;
        try
        {
            if (_eventRepository is not null)
            {
                var query = new EventQuery(CameraId: Id, Limit: 50);
                var items = await Task.Run(async () =>
                {
                    var events = await _eventRepository.QueryAsync(query);
                    var list = new List<EventItemViewModel>(events.Count);
                    foreach (var ev in events)
                    {
                        if (cts.Token.IsCancellationRequested) break;
                        list.Add(EventItemViewModel.Create(ev, _snapshotDirectory));
                    }
                    return list;
                }, cts.Token);

                if (!cts.Token.IsCancellationRequested)
                {
                    CameraEvents.Clear();
                    foreach (var item in items)
                    {
                        CameraEvents.Add(item);
                    }
                    OnPropertyChanged(nameof(HasEvents));
                    OnPropertyChanged(nameof(HasNoEvents));
                    OnPropertyChanged(nameof(LastActivityText));
                    OnPropertyChanged(nameof(TotalDetectionsCount));
                    OnPropertyChanged(nameof(HandRaiseEventsCount));
                    OnPropertyChanged(nameof(PersonPresenceEventsCount));
                    OnPropertyChanged(nameof(ZoneIntrusionEventsCount));
                    OnPropertyChanged(nameof(LineCrossingEventsCount));
                    OnPropertyChanged(nameof(PersonCountingEventsCount));
                    UpdateConfiguredAnalyticsMetrics();
                }
            }

            if (_alertStore is not null && !cts.Token.IsCancellationRequested)
            {
                var alertQuery = new AlertQuery(CameraId: Id, Limit: 50);
                var rawAlerts = await _alertStore.QueryAsync(alertQuery, cts.Token);
                if (!cts.Token.IsCancellationRequested)
                {
                    CameraAlerts.Clear();
                    foreach (var alert in rawAlerts)
                    {
                        CameraAlerts.Add(new AlertItemViewModel(
                            alert,
                            onAcknowledge: async alertId =>
                            {
                                await _alertStore.AcknowledgeAsync(alertId, DateTimeOffset.UtcNow);
                                await RefreshEventsAsync();
                            },
                            onResolve: async alertId =>
                            {
                                await _alertStore.ResolveAsync(alertId, DateTimeOffset.UtcNow);
                                await RefreshEventsAsync();
                            }));
                    }
                    OnPropertyChanged(nameof(HasCameraAlerts));
                    OnPropertyChanged(nameof(HasNoCameraAlerts));
                    OnPropertyChanged(nameof(OpenAlertsCount));
                    OnPropertyChanged(nameof(CriticalAlertsCount));
                    OnPropertyChanged(nameof(LastAlertText));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async Task SaveConfigAsync()
    {
        try
        {
            var req = new CameraWriteRequest(
                Id: Id,
                Name: EditName.Trim(),
                Source: EditSource.Trim(),
                Enabled: EditEnabled);
            var updated = await _cameraService.UpdateAsync(Id, req);
            if (updated is not null)
            {
                Name = updated.Name;
                _source = updated.Source;
                ConfigMessage = "Configuración de cámara guardada correctamente.";
            }
        }
        catch (Exception ex)
        {
            ConfigMessage = $"Error al guardar: {ex.Message}";
        }
    }

    private async Task TestConfigConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(EditSource))
        {
            ConfigMessage = "Debe especificar una fuente de video antes de probar.";
            return;
        }

        IsTestingConfigConnection = true;
        ConfigMessage = "Probando conexión con la fuente de video...";
        try
        {
            var req = new CameraTestRequest(Source: EditSource.Trim());
            var res = await _cameraService.TestAsync(req);
            if (res.Ok)
            {
                ConfigMessage = $"Conexión exitosa: {res.Width}x{res.Height} @ {res.FramesPerSecond:F1} FPS ({res.ConnectionMilliseconds:F0} ms)";
            }
            else
            {
                ConfigMessage = $"Fallo en la prueba: {MainViewModel.TranslateProbeError(res.Error)}";
            }
        }
        catch (Exception ex)
        {
            ConfigMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsTestingConfigConnection = false;
        }
    }

    // Zone Editor Logic
    public void AddZonePoint(NormalizedPoint point)
    {
        if (!IsEditingZones || SelectedZone is not null) return;

        if (IsDrawingRectangle)
        {
            if (DraftPoints.Count == 0)
            {
                DraftPoints = [point];
                ZoneMessage = "Haga clic en la esquina opuesta para definir el rectángulo.";
            }
            else if (DraftPoints.Count == 1)
            {
                var p1 = DraftPoints[0];
                var minX = Math.Min(p1.X, point.X);
                var maxX = Math.Max(p1.X, point.X);
                var minY = Math.Min(p1.Y, point.Y);
                var maxY = Math.Max(p1.Y, point.Y);

                if (Math.Abs(maxX - minX) < 0.01) maxX = Math.Min(1.0, minX + 0.1);
                if (Math.Abs(maxY - minY) < 0.01) maxY = Math.Min(1.0, minY + 0.1);

                DraftPoints =
                [
                    new NormalizedPoint(minX, minY),
                    new NormalizedPoint(maxX, minY),
                    new NormalizedPoint(maxX, maxY),
                    new NormalizedPoint(minX, maxY)
                ];
                ZoneMessage = "Rectángulo definido (4 vértices). Guarde la zona para confirmar.";
            }
            else
            {
                DraftPoints = [point];
                ZoneMessage = "Haga clic en la esquina opuesta para definir el rectángulo.";
            }
        }
        else
        {
            DraftPoints = [.. DraftPoints, point];
            ZoneMessage = null;
        }
    }

    public void BeginZonePointDrag()
    {
        if (!_dragSnapshotTaken)
        {
            _zoneHistory.Push(CloneZones(EditorZones));
            _dragSnapshotTaken = true;
        }
    }

    public void MoveZonePoint(string zoneName, int pointIndex, NormalizedPoint point)
    {
        var zones = CloneZones(EditorZones).ToArray();
        var zoneIndex = Array.FindIndex(zones, zone => zone.Name == zoneName);
        if (zoneIndex < 0 || pointIndex < 0 || pointIndex >= zones[zoneIndex].Points.Count) return;
        var points = zones[zoneIndex].Points.ToArray();
        points[pointIndex] = point;
        zones[zoneIndex] = zones[zoneIndex] with { Points = points };
        try
        {
            EditorZones = zones;
            SelectedZone = zones[zoneIndex];
        }
        catch
        {
        }
    }

    public async Task EndZonePointDragAsync()
    {
        _dragSnapshotTaken = false;
        await SaveZonesAsync();
    }

    private void ToggleZoneEditor()
    {
        IsEditingZones = !IsEditingZones;
        ZoneMessage = IsEditingZones ? "Añade puntos sobre el video o selecciona una zona." : null;
        if (!IsEditingZones)
        {
            DraftPoints = [];
            SelectedZone = null;
        }
    }

    private void StartNewZone()
    {
        SelectedZone = null;
        DraftPoints = [];
        ZoneName = string.Empty;
        ZoneMessage = "Haz clic para añadir al menos 3 puntos.";
    }

    private async Task CloseZoneAsync()
    {
        try
        {
            var candidate = new NormalizedZone(ZoneName.Trim(), DraftPoints.ToArray());
            var zones = EditorZones.Append(candidate).ToArray();
            ZoneEditorLogic.Validate(zones);
            _zoneHistory.Push(CloneZones(EditorZones));
            EditorZones = CloneZones(zones);
            DraftPoints = [];
            SelectedZone = candidate;
            await SaveZonesAsync();
            ZoneMessage = "Zona guardada y aplicada.";
        }
        catch (ArgumentException exception)
        {
            ZoneMessage = exception.Message;
        }
    }

    private async Task UndoZoneAsync()
    {
        if (DraftPoints.Count > 0)
        {
            DraftPoints = DraftPoints.Take(DraftPoints.Count - 1).ToArray();
            return;
        }
        if (_zoneHistory.TryPop(out var previous))
        {
            EditorZones = CloneZones(previous);
            await SaveZonesAsync();
            ZoneMessage = "Cambio deshecho.";
        }
    }

    private async Task DeleteZoneAsync()
    {
        if (SelectedZone is null)
        {
            ZoneMessage = "Selecciona una zona para borrarla.";
            return;
        }
        _zoneHistory.Push(CloneZones(EditorZones));
        EditorZones = CloneZones(EditorZones.Where(zone => zone.Name != SelectedZone.Name).ToArray());
        SelectedZone = null;
        ZoneName = string.Empty;
        await SaveZonesAsync();
        ZoneMessage = "Zona borrada.";
    }

    private async Task SaveZonesAsync()
    {
        try
        {
            var success = await _cameraService.UpdateZonesAsync(Id, EditorZones);
            if (success)
            {
                ZoneMessage = "Zonas guardadas correctamente.";
            }
            else
            {
                ZoneMessage = "No fue posible guardar las zonas en el servicio.";
            }
        }
        catch (Exception exception)
        {
            ZoneMessage = $"Error al guardar zonas: {exception.Message}";
        }
    }

    private static IReadOnlyList<NormalizedZone> CloneZones(IReadOnlyList<NormalizedZone> zones) =>
        zones.Select(zone => new NormalizedZone(zone.Name, zone.Points.ToArray())).ToArray();

    private static string SanitizeSource(string source)
    {
        if (!source.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            return source;
        }

        try
        {
            var uri = new UriBuilder(source) { UserName = string.Empty, Password = string.Empty };
            return uri.Uri.AbsoluteUri;
        }
        catch
        {
            return "RTSP";
        }
    }

    private static string DetectSourceType(string source)
    {
        if (int.TryParse(source, out _)) return "USB (Cámara web)";
        if (source.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase)) return "RTSP (IP Streaming)";
        if (source.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".avi", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
            return "Archivo de video";
        return "Fuente personalizada";
    }

    public async ValueTask DisposeAsync()
    {
        await StopStreamingAsync();
        _eventsCancellation?.Cancel();
        _eventBusCancellation?.Cancel();
        if (_eventBusSubscription is not null)
        {
            try { await _eventBusSubscription.DisposeAsync(); } catch { }
            _eventBusSubscription = null;
        }
        if (_eventBusTask is not null)
        {
            try { await _eventBusTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
            _eventBusTask = null;
        }
        _eventBusCancellation?.Dispose();
        _eventBusCancellation = null;
        _lifecycle.Dispose();
    }
}

public sealed record RuleEventOption(string Id, string DisplayName);
