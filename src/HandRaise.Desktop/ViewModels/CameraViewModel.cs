using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HandRaise.Application.Zones;
using HandRaise.Host.Services;

namespace HandRaise.Desktop.ViewModels;

public sealed class CameraViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ICameraManagementService _cameraService;
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
        CameraView camera,
        ICameraManagementService cameraService,
        Dispatcher dispatcher)
    {
        _id = camera.Id;
        _name = camera.Name;
        _source = camera.Source;
        _autoStart = camera.Enabled;
        _cameraService = cameraService;
        _dispatcher = dispatcher;

        UpdateFromView(camera);

        ToggleCommand = new AsyncRelayCommand(ToggleAsync);
        ToggleZoneEditorCommand = new RelayCommand(ToggleZoneEditor);
        CloseZoneCommand = new AsyncRelayCommand(CloseZoneAsync);
        UndoZoneCommand = new AsyncRelayCommand(UndoZoneAsync);
        DeleteZoneCommand = new AsyncRelayCommand(DeleteZoneAsync);
        NewZoneCommand = new RelayCommand(StartNewZone);
    }

    public string Id => _id;
    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string SourceDescription => SanitizeSource(_source);
    public bool AutoStart => _autoStart;

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string? Error { get => _error; private set => SetProperty(ref _error, value); }
    public double FramesPerSecond { get => _framesPerSecond; private set => SetProperty(ref _framesPerSecond, value); }
    public ImageSource? VideoImage { get => _videoImage; private set => SetProperty(ref _videoImage, value); }
    public int FrameWidth { get => _frameWidth; private set => SetProperty(ref _frameWidth, value); }
    public int FrameHeight { get => _frameHeight; private set => SetProperty(ref _frameHeight, value); }

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
    public IAsyncRelayCommand ToggleCommand { get; }

    public IRelayCommand ToggleZoneEditorCommand { get; }
    public IAsyncRelayCommand CloseZoneCommand { get; }
    public IAsyncRelayCommand UndoZoneCommand { get; }
    public IAsyncRelayCommand DeleteZoneCommand { get; }
    public IRelayCommand NewZoneCommand { get; }

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

    public void UpdateFromView(CameraView view)
    {
        Name = view.Name;
        _source = view.Source;
        OnPropertyChanged(nameof(SourceDescription));

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
                            // Ignorar frame corrupto
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
                _zonesInitialized = true;
            }
        }
        catch (Exception ex)
        {
            ZoneMessage = $"Error al cargar zonas: {ex.Message}";
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

    public async ValueTask DisposeAsync()
    {
        await StopStreamingAsync();
        _lifecycle.Dispose();
    }
}
