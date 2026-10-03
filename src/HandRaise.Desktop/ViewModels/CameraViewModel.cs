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
using HandRaise.Application.Storage;
using HandRaise.Application.Zones;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Lines;
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

    private ImageSource? _videoImage;
    private string _status = "Desconectada";
    private string? _error;
    private double _framesPerSecond;
    private bool _isRunning;
    private bool _isStreaming;
    private int _frameWidth;
    private int _frameHeight;
    private int _openAlertsCount;

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

    // Camera Configuration Editing
    private string _editName;
    private string _editSource;
    private bool _editEnabled;
    private string? _configMessage;

    // Runtime Plan
    private string _runtimePlanSummary = "Sin analíticas activas";
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
        string snapshotDirectory = "data/snapshots")
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
        _snapshotDirectory = snapshotDirectory;
        _dispatcher = dispatcher;

        AssignedAnalytics = [];
        AvailableCatalog = [];
        CameraEvents = [];

        UpdateFromView(camera);

        ToggleCommand = new AsyncRelayCommand(ToggleAsync);
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

        // Config commands
        SaveConfigCommand = new AsyncRelayCommand(SaveConfigAsync);
        RefreshEventsCommand = new AsyncRelayCommand(RefreshEventsAsync);
    }

    public string Id => _id;
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string SourceDescription => SanitizeSource(_source);
    public string SourceType => DetectSourceType(_source);
    public bool AutoStart => _autoStart;

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string? Error { get => _error; private set => SetProperty(ref _error, value); }
    public double FramesPerSecond { get => _framesPerSecond; private set => SetProperty(ref _framesPerSecond, value); }
    public ImageSource? VideoImage { get => _videoImage; private set => SetProperty(ref _videoImage, value); }
    public int FrameWidth { get => _frameWidth; private set => SetProperty(ref _frameWidth, value); }
    public int FrameHeight { get => _frameHeight; private set => SetProperty(ref _frameHeight, value); }
    public int OpenAlertsCount { get => _openAlertsCount; set => SetProperty(ref _openAlertsCount, value); }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(ButtonText));
            }
        }
    }

    public string ButtonText => IsRunning ? "Detener" : "Iniciar";
    public int ActiveAnalyticsCount => AssignedAnalytics.Count(a => a.Enabled);

    // Detail Tabs
    public int DetailTabIndex
    {
        get => _detailTabIndex;
        set
        {
            if (SetProperty(ref _detailTabIndex, value))
            {
                OnPropertyChanged(nameof(IsResumenTabSelected));
                OnPropertyChanged(nameof(IsAnalyticsTabSelected));
                OnPropertyChanged(nameof(IsZonesTabSelected));
                OnPropertyChanged(nameof(IsEventsTabSelected));
                OnPropertyChanged(nameof(IsConfigTabSelected));
                OnPropertyChanged(nameof(IsRuntimePlanTabSelected));
            }
        }
    }

    public bool IsResumenTabSelected { get => _detailTabIndex == 0; set { if (value) DetailTabIndex = 0; } }
    public bool IsAnalyticsTabSelected { get => _detailTabIndex == 1; set { if (value) DetailTabIndex = 1; } }
    public bool IsZonesTabSelected { get => _detailTabIndex == 2; set { if (value) DetailTabIndex = 2; } }
    public bool IsEventsTabSelected { get => _detailTabIndex == 3; set { if (value) DetailTabIndex = 3; } }
    public bool IsConfigTabSelected { get => _detailTabIndex == 4; set { if (value) DetailTabIndex = 4; } }
    public bool IsRuntimePlanTabSelected { get => _detailTabIndex == 5; set { if (value) DetailTabIndex = 5; } }

    // Collections
    public ObservableCollection<CameraAnalyticItemViewModel> AssignedAnalytics { get; }
    public ObservableCollection<AnalyticDefinition> AvailableCatalog { get; }
    public ObservableCollection<EventItemViewModel> CameraEvents { get; }

    public AnalyticDefinition? SelectedCatalogAnalytic
    {
        get => _selectedCatalogAnalytic;
        set => SetProperty(ref _selectedCatalogAnalytic, value);
    }

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
    public IReadOnlyList<NormalizedZone> EditorZones { get => _editorZones; private set => SetProperty(ref _editorZones, value); }
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

    public IReadOnlyList<LineDefinition> EditorLines { get => _editorLines; private set => SetProperty(ref _editorLines, value); }
    public LineDefinition? SelectedLine { get => _selectedLine; set => SetProperty(ref _selectedLine, value); }
    public string NewLineName { get => _newLineName; set => SetProperty(ref _newLineName, value); }
    public LineDirectionMode SelectedLineDirection { get => _selectedLineDirection; set => SetProperty(ref _selectedLineDirection, value); }
    public string? LineMessage { get => _lineMessage; private set => SetProperty(ref _lineMessage, value); }

    // Config editing
    public string EditName { get => _editName; set => SetProperty(ref _editName, value); }
    public string EditSource { get => _editSource; set => SetProperty(ref _editSource, value); }
    public bool EditEnabled { get => _editEnabled; set => SetProperty(ref _editEnabled, value); }
    public string? ConfigMessage { get => _configMessage; private set => SetProperty(ref _configMessage, value); }

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
    public IAsyncRelayCommand RefreshAnalyticsCommand { get; }
    public IAsyncRelayCommand SaveConfigCommand { get; }
    public IAsyncRelayCommand RefreshEventsCommand { get; }

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

    private async Task ToggleAsync()
    {
        try
        {
            if (IsRunning)
            {
                await _cameraService.StopAsync(Id);
                await StopStreamingAsync();
                IsRunning = false;
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
                    await _dispatcher.InvokeAsync(() =>
                    {
                        Status = "En línea";
                        Error = null;
                    });

                    await foreach (var jpeg in subResult.Reader.ReadAllAsync(token))
                    {
                        try
                        {
                            var bitmap = DecodeJpeg(jpeg);
                            frameCount++;
                            if (fpsStopwatch.ElapsedMilliseconds >= 1000)
                            {
                                var currentFps = frameCount * 1000.0 / fpsStopwatch.ElapsedMilliseconds;
                                frameCount = 0;
                                fpsStopwatch.Restart();
                                await _dispatcher.InvokeAsync(() => FramesPerSecond = Math.Round(currentFps, 1));
                            }

                            await _dispatcher.InvokeAsync(() =>
                            {
                                VideoImage = bitmap;
                                FrameWidth = bitmap.PixelWidth;
                                FrameHeight = bitmap.PixelHeight;
                                Status = "En línea";
                                Error = null;
                            });
                        }
                        catch
                        {
                        }
                    }
                }
                else
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        Status = subResult.Status switch
                        {
                            CameraStreamStatus.LimitReached => "Límite de clientes alcanzado",
                            CameraStreamStatus.NotFound => "Cámara no encontrada",
                            _ => "Esperando video..."
                        };
                        if (subResult.Error is not null) Error = subResult.Error;
                    });
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    Status = "Sin señal / reconectando";
                    Error = ex.Message;
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

    public async Task InitializeZonesAsync()
    {
        if (_zonesInitialized) return;
        try
        {
            var saved = await _cameraService.GetZonesAsync(Id);
            if (saved is not null)
            {
                EditorZones = saved;
            }
            if (_lineStore is not null)
            {
                EditorLines = await _lineStore.ListByCameraAsync(Id);
            }
            _zonesInitialized = true;
        }
        catch (Exception ex)
        {
            ZoneMessage = $"Error al cargar zonas/líneas: {ex.Message}";
        }
    }

    public async Task LoadAssignedAnalyticsAsync()
    {
        if (_analyticService is null) return;
        try
        {
            var instances = await _analyticService.ListByCameraAsync(Id) ?? [];
            var catalog = _analyticCatalog?.GetAll() ?? [];

            AvailableCatalog.Clear();
            foreach (var item in catalog)
            {
                AvailableCatalog.Add(item);
            }
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
                        UpdateRuntimePlan();
                        OnPropertyChanged(nameof(ActiveAnalyticsCount));
                    },
                    onRemove: async (instanceId) =>
                    {
                        await _analyticService.DeleteAsync(Id, instanceId);
                        await LoadAssignedAnalyticsAsync();
                    }));
            }
            UpdateRuntimePlan();
            OnPropertyChanged(nameof(ActiveAnalyticsCount));
        }
        catch
        {
        }
    }

    private void UpdateRuntimePlan()
    {
        var active = AssignedAnalytics.Where(a => a.Enabled).ToList();
        if (active.Count == 0)
        {
            RuntimePlanSummary = "Sin analíticas activas";
            RequiredCapabilitiesSummary = "Ninguna";
            ModelProviderSummary = "Inactivo";
            return;
        }

        var caps = active.Select(a => a.CapabilitiesSummary).Distinct();
        RequiredCapabilitiesSummary = string.Join(", ", caps);
        RuntimePlanSummary = $"{active.Count} analítica(s) activa(s): {string.Join(", ", active.Select(a => a.DisplayName))}";
        ModelProviderSummary = "YoloPoseCapabilityProvider (DirectML / CPU Fallback)";
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

    public async Task RefreshEventsAsync()
    {
        if (_eventRepository is null) return;
        try
        {
            var events = await _eventRepository.QueryAsync(new EventQuery(CameraId: Id, Limit: 50));
            CameraEvents.Clear();
            foreach (var ev in events)
            {
                CameraEvents.Add(EventItemViewModel.Create(ev, _snapshotDirectory));
            }
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
                ConfigMessage = "Configuración de cámara guardada.";
            }
        }
        catch (Exception ex)
        {
            ConfigMessage = $"Error al guardar: {ex.Message}";
        }
    }

    // Zone Editor Logic
    public void AddZonePoint(NormalizedPoint point)
    {
        if (!IsEditingZones || SelectedZone is not null) return;
        DraftPoints = [.. DraftPoints, point];
        ZoneMessage = null;
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
        _lifecycle.Dispose();
    }
}
