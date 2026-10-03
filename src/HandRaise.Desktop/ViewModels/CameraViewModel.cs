using System.Diagnostics;
using System.Threading.Channels;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandRaise.Application.Capture;
using HandRaise.Application.Events;
using HandRaise.Application.Inference;
using HandRaise.Application.Pipelines;
using HandRaise.Application.Storage;
using HandRaise.Application.Tracking;
using HandRaise.Desktop.Configuration;
using HandRaise.Infrastructure.Windows.Capture;
using HandRaise.Infrastructure.Windows.Overlay;
using HandRaise.Infrastructure.Windows.Storage;
using HandRaise.Application.Zones;

namespace HandRaise.Desktop.ViewModels;

public sealed class CameraViewModel : ObservableObject, IAsyncDisposable
{
    private readonly CameraSettings _camera;
    private readonly DesktopConfiguration _configuration;
    private readonly IInferenceBackend _backend;
    private readonly HandEventBus _eventBus;
    private readonly IZoneSettingsStore _zoneStore;
    private readonly AtomicZoneProvider _zoneProvider;
    private readonly Func<long> _backendGeneration;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _sessionCancellation;
    private IVideoSource? _source;
    private Task? _pipelineTask;
    private Task? _renderTask;
    private Task? _watchdogTask;
    private WriteableBitmap? _videoImage;
    private string _status = "Desconectada";
    private string? _error;
    private double _framesPerSecond;
    private bool _isRunning;
    private long _lastFrameTicks;
    private int _frameWidth;
    private int _frameHeight;
    private bool _zonesInitialized;
    private bool _isEditingZones;
    private string _zoneName = string.Empty;
    private string? _zoneMessage;
    private NormalizedZone? _selectedZone;
    private IReadOnlyList<NormalizedZone> _editorZones = [];
    private IReadOnlyList<NormalizedPoint> _draftPoints = [];
    private readonly Stack<IReadOnlyList<NormalizedZone>> _zoneHistory = new();
    private bool _dragSnapshotTaken;

    public CameraViewModel(
        CameraSettings camera,
        DesktopConfiguration configuration,
        IInferenceBackend backend,
        HandEventBus eventBus,
        IZoneSettingsStore zoneStore,
        Func<long> backendGeneration,
        Dispatcher dispatcher)
    {
        _camera = camera;
        _configuration = configuration;
        _backend = backend;
        _eventBus = eventBus;
        _zoneStore = zoneStore;
        _zoneProvider = new AtomicZoneProvider(
            camera.Zones,
            string.Equals(camera.ZonesCoordinateSpace, "normalized", StringComparison.OrdinalIgnoreCase));
        _backendGeneration = backendGeneration;
        _dispatcher = dispatcher;
        ToggleCommand = new AsyncRelayCommand(ToggleAsync);
        ToggleZoneEditorCommand = new RelayCommand(ToggleZoneEditor);
        CloseZoneCommand = new AsyncRelayCommand(CloseZoneAsync);
        UndoZoneCommand = new AsyncRelayCommand(UndoZoneAsync);
        DeleteZoneCommand = new AsyncRelayCommand(DeleteZoneAsync);
        NewZoneCommand = new RelayCommand(StartNewZone);
    }

    public string Id => _camera.Id;
    public string Name => _camera.Name;
    public string SourceDescription => SanitizeSource(_camera.Source);
    public IAsyncRelayCommand ToggleCommand { get; }
    public bool AutoStart => _camera.Enabled;
    public IRelayCommand ToggleZoneEditorCommand { get; }
    public IAsyncRelayCommand CloseZoneCommand { get; }
    public IAsyncRelayCommand UndoZoneCommand { get; }
    public IAsyncRelayCommand DeleteZoneCommand { get; }
    public IRelayCommand NewZoneCommand { get; }

    public bool IsEditingZones
    {
        get => _isEditingZones;
        private set { if (SetProperty(ref _isEditingZones, value)) OnPropertyChanged(nameof(ZoneEditorButtonText)); }
    }

    public string ZoneEditorButtonText => IsEditingZones ? "Salir del editor" : "Editar zonas";
    public string ZoneName { get => _zoneName; set => SetProperty(ref _zoneName, value); }
    public string? ZoneMessage { get => _zoneMessage; private set => SetProperty(ref _zoneMessage, value); }
    public IReadOnlyList<NormalizedZone> EditorZones { get => _editorZones; private set => SetProperty(ref _editorZones, value); }
    public IReadOnlyList<NormalizedPoint> DraftPoints { get => _draftPoints; private set => SetProperty(ref _draftPoints, value); }
    public NormalizedZone? SelectedZone
    {
        get => _selectedZone;
        set { if (SetProperty(ref _selectedZone, value) && value is not null) ZoneName = value.Name; }
    }

    public async Task InitializeZonesAsync()
    {
        var saved = await _zoneStore.LoadAsync(Id);
        if (saved is not null)
        {
            _zoneProvider.Replace(saved);
            EditorZones = saved;
            _zonesInitialized = true;
        }
    }

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
            _zoneProvider.Replace(zones);
            EditorZones = zones;
            SelectedZone = zones[zoneIndex];
            ZoneMessage = null;
        }
        catch (ArgumentException exception)
        {
            ZoneMessage = exception.Message;
        }
    }

    public async Task EndZonePointDragAsync()
    {
        if (!_dragSnapshotTaken) return;
        _dragSnapshotTaken = false;
        try
        {
            ZoneEditorLogic.Validate(EditorZones);
            await SaveZonesAsync();
        }
        catch (ArgumentException exception)
        {
            var previous = _zoneHistory.Pop();
            ApplyZones(previous);
            ZoneMessage = exception.Message;
        }
    }

    public WriteableBitmap? VideoImage
    {
        get => _videoImage;
        private set => SetProperty(ref _videoImage, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public double FramesPerSecond
    {
        get => _framesPerSecond;
        private set => SetProperty(ref _framesPerSecond, value);
    }

    public int FrameWidth => _frameWidth;
    public int FrameHeight => _frameHeight;

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

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (IsRunning)
            {
                return;
            }

            Error = null;
            Status = "Conectando";
            IsRunning = true;
            _sessionCancellation = new CancellationTokenSource();
            _source = CreateSource();
            var frames = Channel.CreateBounded<UiFrame>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
            var pipeline = new CameraPipeline(
                _camera.Id,
                _source,
                _backend,
                new OpenCvOverlayRenderer(_configuration.Hands.KeypointConfidence),
                _configuration.CreateDetectionOptions(),
                _camera.Zones,
                _configuration.Model.Name,
                new ByteTracker(_configuration.CreateTrackerOptions()),
                eventPublisher: _eventBus,
                backendGeneration: _backendGeneration,
                snapshotEncoder: new OpenCvPersonSnapshotEncoder(),
                snapshotOptions: _configuration.CreateSnapshotEncodingOptions(),
                log: message => SetError(message),
                zoneProvider: _zoneProvider);
            _lastFrameTicks = Stopwatch.GetTimestamp();
            _pipelineTask = Task.Run(() => RunPipelineAsync(
                pipeline, frames, _sessionCancellation.Token));
            _renderTask = Task.Run(() => RenderFramesAsync(frames.Reader, _sessionCancellation.Token));
            _watchdogTask = Task.Run(() => WatchStatusAsync(_sessionCancellation.Token));
        }
        catch
        {
            IsRunning = false;
            Status = "Desconectada";
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (!IsRunning)
            {
                return;
            }

            _sessionCancellation?.Cancel();
            var tasks = new[] { _pipelineTask, _renderTask, _watchdogTask }
                .Where(task => task is not null)
                .Cast<Task>();
            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
            }

            if (_source is not null)
            {
                await _source.DisposeAsync();
            }

            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
            _source = null;
            _pipelineTask = null;
            _renderTask = null;
            _watchdogTask = null;
            IsRunning = false;
            FramesPerSecond = 0;
            Status = "Desconectada";
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycle.Dispose();
    }

    private async Task ToggleAsync()
    {
        try
        {
            if (IsRunning)
            {
                await StopAsync();
            }
            else
            {
                await StartAsync();
            }
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private async Task RunPipelineAsync(
        CameraPipeline pipeline,
        Channel<UiFrame> frames,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var result in pipeline.RunAsync(cancellationToken))
            {
                using (result)
                {
                    var frame = new UiFrame(
                        result.Frame.Width,
                        result.Frame.Height,
                        result.Frame.Pixels.ToArray(),
                        result.Metrics.FramesPerSecond);
                    if (!frames.Writer.TryWrite(frame))
                    {
                        frames.Reader.TryRead(out _);
                        frames.Writer.TryWrite(frame);
                    }

                    Interlocked.Exchange(ref _lastFrameTicks, Stopwatch.GetTimestamp());
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
        finally
        {
            frames.Writer.TryComplete();
        }
    }

    private async Task RenderFramesAsync(
        ChannelReader<UiFrame> frames,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in frames.ReadAllAsync(cancellationToken))
            {
                await _dispatcher.InvokeAsync(() => Render(frame));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task WatchStatusAsync(CancellationToken cancellationToken)
    {
        var offlineAfter = TimeSpan.FromMilliseconds(
            Math.Max(2000, _configuration.Capture.ReadTimeoutMs * 2));
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastFrameTicks)) > offlineAfter)
                {
                    await _dispatcher.InvokeAsync(() => Status = "Sin señal / reconectando");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Render(UiFrame frame)
    {
        _frameWidth = frame.Width;
        _frameHeight = frame.Height;
        OnPropertyChanged(nameof(FrameWidth));
        OnPropertyChanged(nameof(FrameHeight));
        if (!_zonesInitialized)
        {
            EditorZones = _zoneProvider.GetNormalized(frame.Width, frame.Height);
            _zoneProvider.Replace(EditorZones);
            _zonesInitialized = true;
        }
        if (VideoImage is null || VideoImage.PixelWidth != frame.Width ||
            VideoImage.PixelHeight != frame.Height)
        {
            VideoImage = new WriteableBitmap(
                frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
        }

        VideoImage.WritePixels(
            new System.Windows.Int32Rect(0, 0, frame.Width, frame.Height),
            frame.Pixels,
            frame.Width * 3,
            0);
        FramesPerSecond = frame.FramesPerSecond;
        Status = "En línea";
        Error = null;
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
            ApplyZones(zones);
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
            ApplyZones(previous);
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
        ApplyZones(EditorZones.Where(zone => zone.Name != SelectedZone.Name).ToArray());
        SelectedZone = null;
        ZoneName = string.Empty;
        await SaveZonesAsync();
        ZoneMessage = "Zona borrada.";
    }

    private void ApplyZones(IReadOnlyList<NormalizedZone> zones)
    {
        _zoneProvider.Replace(zones);
        EditorZones = CloneZones(zones);
    }

    private async Task SaveZonesAsync()
    {
        try
        {
            await _zoneStore.SaveAsync(Id, EditorZones);
        }
        catch (Exception exception)
        {
            ZoneMessage = $"Las zonas se aplicaron, pero no se pudieron guardar: {exception.Message}";
        }
    }

    private static IReadOnlyList<NormalizedZone> CloneZones(IReadOnlyList<NormalizedZone> zones) =>
        zones.Select(zone => new NormalizedZone(zone.Name, zone.Points.ToArray())).ToArray();

    private IVideoSource CreateSource()
    {
        var options = _configuration.CreateCaptureOptions(_camera.Loop);
        Action<string> log = HandleCaptureLog;
        if (int.TryParse(_camera.Source, out var index))
        {
            return new WebcamVideoSource(index, options, log);
        }

        if (_camera.Source.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            return new RtspVideoSource(_camera.Source, options, log);
        }

        return new FileVideoSource(_camera.Source, options);
    }

    private void SetError(string message) => _dispatcher.BeginInvoke(() =>
    {
        Error = message;
        Status = "Fuera de línea";
    });

    private void HandleCaptureLog(string message) => _dispatcher.BeginInvoke(() =>
    {
        if (message.Contains("conectada", StringComparison.OrdinalIgnoreCase))
        {
            Status = "En línea";
            Error = null;
        }
        else if (message.Contains("perdió", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("reintentar", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            Status = "Sin señal / reconectando";
            Error = message;
        }
    });

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

    private sealed record UiFrame(int Width, int Height, byte[] Pixels, double FramesPerSecond);
}
