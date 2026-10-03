using System.Collections.Concurrent;
using System.Diagnostics;
using HandRaise.Application.Capture;
using HandRaise.Application.Events;
using HandRaise.Application.Hardware;
using HandRaise.Application.Inference;
using HandRaise.Application.Overlay;
using HandRaise.Application.Pipelines;
using HandRaise.Application.Storage;
using HandRaise.Application.Tracking;
using HandRaise.Application.Zones;
using HandRaise.Domain.Detection;
using HandRaise.Domain.Zones;
using HandRaise.Host.Configuration;
using HandRaise.Infrastructure.Windows.Capture;
using HandRaise.Infrastructure.Windows.Diagnostics;
using HandRaise.Infrastructure.Windows.Hardware;
using HandRaise.Infrastructure.Windows.Inference;
using HandRaise.Infrastructure.Windows.Overlay;
using HandRaise.Infrastructure.Windows.Settings;
using HandRaise.Infrastructure.Windows.Storage;
using AppHostOptions = HandRaise.Host.Configuration.HostOptions;
using System.Threading.Channels;

namespace HandRaise.Host.Services;

public sealed class HeadlessEngineService(
    AppHostOptions options,
    PipelineMetricsRegistry metrics,
    HostRuntimeState runtimeState,
    IHandEventRepository repository,
    HandEventBus eventBus,
    ICameraStore cameraStore,
    ICameraCredentialStore credentialStore,
    ILogger<HeadlessEngineService> logger) : BackgroundService, ICameraManagementService
{
    private readonly ConcurrentDictionary<string, CameraSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SwitchableInferenceBackend? _backend;
    private HardwareInventory _inventory = new([], RuntimeInfo.DevelopmentFallback, []);
    private DevicePreferenceService? _preferences;
    private CancellationToken _hostToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _hostToken = stoppingToken;
        await StorageDatabase.MigrateAsync(StorageDatabase.CreateOptions(options.Storage.DatabasePath), stoppingToken);
        await using var persistence = new EventPersistenceWorker(eventBus, repository,
            new SnapshotStore(options.Storage.SnapshotDirectory), StorageWorkerOptions(),
            log: message => logger.LogWarning("{Message}", RollingErrorLog.Sanitize(message)));
        try
        {
            _inventory = await new WindowsDeviceDetector(TimeSpan.FromSeconds(3)).DetectAsync(stoppingToken);
            _preferences = new DevicePreferenceService(new JsonUserSettingsStore());
            var selection = await _preferences.InitializeAsync(_inventory.Devices, stoppingToken);
            var model = Model();
            var integrity = await ModelIntegrityVerifier.VerifyAsync(model.Path,
                Path.Combine(Path.GetDirectoryName(model.Path)!, "model.manifest.json"), stoppingToken);
            if (!integrity.Success) throw new InvalidOperationException(integrity.Message);
            var factory = new WindowsMlBackendFactory();
            _backend = await LoadBackendAsync(selection.Device, _inventory.Devices, model, factory, _preferences, stoppingToken);
            runtimeState.Set(_inventory, _backend.Device.Id, options.Model.Name);
            await cameraStore.InitializeAsync(SeedCameras(), stoppingToken);
            foreach (var camera in await cameraStore.ListAsync(stoppingToken))
                if (camera.Enabled) await StartCoreAsync(camera, stoppingToken);
            _ready.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { _ready.TrySetCanceled(stoppingToken); }
        catch (Exception exception) { _ready.TrySetException(exception); throw; }
        finally
        {
            foreach (var id in _sessions.Keys.ToArray()) await StopCoreAsync(id);
            await eventBus.DisposeAsync();
            if (_backend is not null) await _backend.DisposeAsync();
            _lifecycle.Dispose();
        }
    }

    public async Task<IReadOnlyList<CameraView>> ListAsync(CancellationToken token = default)
    {
        await Ready(token);
        return (await cameraStore.ListAsync(token)).Select(View).ToArray();
    }

    public async Task<CameraView?> GetAsync(string id, CancellationToken token = default)
    {
        await Ready(token);
        var camera = await cameraStore.GetAsync(id, token);
        return camera is null ? null : View(camera);
    }

    public async Task<CameraView> CreateAsync(CameraWriteRequest request, CancellationToken token = default)
    {
        await Ready(token);
        var (camera, credentials) = Prepare(request, request.Id, []);
        await cameraStore.AddAsync(camera, token);
        if (credentials is not null) await credentialStore.SaveAsync(camera.Id, credentials, token);
        if (camera.Enabled) await StartCoreAsync(camera, token);
        return View(camera);
    }

    public async Task<CameraView?> UpdateAsync(string id, CameraWriteRequest request, CancellationToken token = default)
    {
        await Ready(token);
        var existing = await cameraStore.GetAsync(id, token);
        if (existing is null) return null;
        var (camera, credentials) = Prepare(request, id, existing.Zones);
        await StopCoreAsync(id);
        await cameraStore.UpdateAsync(camera, token);
        if (credentials is not null) await credentialStore.SaveAsync(id, credentials, token);
        if (camera.Enabled) await StartCoreAsync(camera, token);
        return View(camera);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken token = default)
    {
        await Ready(token);
        if (await cameraStore.GetAsync(id, token) is null) return false;
        await StopCoreAsync(id);
        await credentialStore.DeleteAsync(id, token);
        metrics.Remove(id);
        return await cameraStore.DeleteAsync(id, token);
    }

    public async Task<CameraView?> StartAsync(string id, CancellationToken token = default) =>
        await SetRunningAsync(id, true, token);

    public async Task<CameraView?> StopAsync(string id, CancellationToken token = default) =>
        await SetRunningAsync(id, false, token);

    private async Task<CameraView?> SetRunningAsync(string id, bool running, CancellationToken token)
    {
        await Ready(token);
        var camera = await cameraStore.GetAsync(id, token);
        if (camera is null) return null;
        if (camera.Enabled != running)
        {
            camera = camera with { Enabled = running };
            await cameraStore.UpdateAsync(camera, token);
        }
        if (running) await StartCoreAsync(camera, token); else await StopCoreAsync(id);
        return View(camera);
    }

    public async Task<byte[]?> GetSnapshotAsync(string id, CancellationToken token = default)
    {
        await Ready(token);
        return _sessions.TryGetValue(id, out var session) ? session.GetSnapshot() : null;
    }

    public async Task<IReadOnlyList<NormalizedZone>?> GetZonesAsync(string id, CancellationToken token = default)
    {
        await Ready(token);
        return (await cameraStore.GetAsync(id, token))?.Zones;
    }

    public async Task<bool> UpdateZonesAsync(string id, IReadOnlyList<NormalizedZone> zones, CancellationToken token = default)
    {
        await Ready(token);
        ZoneEditorLogic.Validate(zones);
        var camera = await cameraStore.GetAsync(id, token);
        if (camera is null) return false;
        var copy = zones.Select(x => new NormalizedZone(x.Name.Trim(), x.Points.ToArray())).ToArray();
        if (!await cameraStore.UpdateAsync(camera with { Zones = copy }, token)) return false;
        if (_sessions.TryGetValue(id, out var session)) session.Zones.Replace(copy);
        return true;
    }

    public async Task<DeviceChangeResult> ChangeDeviceAsync(string deviceId, CancellationToken token = default)
    {
        await Ready(token);
        var backend = _backend ?? throw new InvalidOperationException("El motor aún no está disponible.");
        var device = _inventory.Devices.FirstOrDefault(x => string.Equals(x.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        if (device is null) throw new KeyNotFoundException($"No existe el dispositivo '{deviceId}'.");
        if (!device.RuntimeAvailable) return new(false, backend.Device.Id, device.UnavailableReason, 0);
        var result = await backend.SwitchAsync(device, token);
        if (result.Success)
        {
            runtimeState.SetActiveDevice(result.ActiveDevice.Id);
            if (_preferences is not null) await _preferences.SelectAsync(_inventory.Devices, result.ActiveDevice.Id, token);
        }
        return new(result.Success, result.ActiveDevice.Id,
            result.Error is null ? null : RollingErrorLog.Sanitize(result.Error), result.Duration.TotalMilliseconds);
    }

    public async Task<CameraTestResult> TestAsync(CameraTestRequest request, CancellationToken token = default)
    {
        await Ready(token);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var (source, credentials) = CameraSource.Sanitize(request.Source, request.Username, request.Password);
            await using var video = CreateSource(new("test", "test", source, false, false, []), credentials);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(options.Capture.OpenTimeoutMs + options.Capture.ReadTimeoutMs);
            await foreach (var frame in video.ReadAllAsync(timeout.Token))
            {
                using (frame) return new(true, null, frame.Width, frame.Height, video.FramesPerSecond,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            return new(false, "La fuente no entregó frames.", null, null, null, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
        {
            return new(false, RollingErrorLog.Sanitize(exception.Message), null, null, null,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    public async Task<CameraStreamSubscriptionResult> SubscribeStreamAsync(string id, double fps, int quality, CancellationToken token = default)
    {
        await Ready(token);
        if (!_sessions.TryGetValue(id, out var session))
        {
            var camera = await cameraStore.GetAsync(id, token);
            if (camera is null) return new(CameraStreamStatus.NotFound, Error: $"Cámara '{id}' no encontrada.");
            return new(CameraStreamStatus.Offline, Error: $"Cámara '{id}' no está en ejecución.");
        }

        var (status, subscriber, error) = session.SubscribeStream(
            options.Streaming.MaxStreamClientsPerCamera, fps, quality);

        if (status != CameraStreamStatus.Success || subscriber is null)
        {
            return new(status, Error: error);
        }

        return new(CameraStreamStatus.Success, subscriber.Channel.Reader, subscriber);
    }

    private async Task StartCoreAsync(CameraDefinition camera, CancellationToken token)
    {
        await _lifecycle.WaitAsync(token);
        try
        {
            if (_sessions.ContainsKey(camera.Id)) return;
            var zones = new AtomicZoneProvider([], true);
            zones.Replace(camera.Zones);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_hostToken);
            var session = new CameraSession(cancellation, zones);
            metrics.Register(camera.Id, camera.Name);
            if (!_sessions.TryAdd(camera.Id, session)) { cancellation.Dispose(); return; }
            session.Task = RunCameraAsync(camera, session, cancellation.Token);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync(string id)
    {
        if (!_sessions.TryRemove(id, out var session)) { metrics.SetOffline(id); return; }
        session.Cancellation.Cancel();
        try { if (session.Task is not null) await session.Task; } catch (OperationCanceledException) { }
        session.Dispose();
        metrics.SetOffline(id);
    }

    private async Task RunCameraAsync(CameraDefinition camera, CameraSession session, CancellationToken token)
    {
        try
        {
            var credentials = await credentialStore.GetAsync(camera.Id, token);
            await using var source = CreateSource(camera, credentials);
            var backend = _backend ?? throw new InvalidOperationException("Backend no disponible.");
            var pipeline = new CameraPipeline(camera.Id, source, backend,
                new OpenCvOverlayRenderer(options.Hands.KeypointConfidence),
                Detection(), [],
                options.Model.Name, new ByteTracker(Tracker()),
                eventPublisher: new ContextualEventPublisher(eventBus, options.NodeId, options.SiteId),
                backendGeneration: () => backend.Generation, snapshotEncoder: new OpenCvPersonSnapshotEncoder(),
                snapshotOptions: new(options.Storage.SnapshotMarginRatio, options.Storage.JpegQuality),
                log: message => logger.LogWarning("Cámara {Camera}: {Message}", camera.Id, RollingErrorLog.Sanitize(message)),
                zoneProvider: session.Zones);
            await foreach (var frame in pipeline.RunAsync(token))
            {
                using (frame)
                {
                    metrics.Update(camera.Id, frame.Metrics);
                    session.OnFrame(frame.Frame, options.Storage.JpegQuality);
                }
            }
            metrics.SetOffline(camera.Id, "Fuente finalizada.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { metrics.SetOffline(camera.Id); }
        catch (Exception exception)
        {
            var message = RollingErrorLog.Sanitize(exception.Message);
            metrics.SetOffline(camera.Id, message);
            logger.LogError("Cámara {Camera}: {Message}", camera.Id, message);
        }
    }

    private IVideoSource CreateSource(CameraDefinition camera, CameraCredentials? credentials)
    {
        var capture = new HandRaise.Infrastructure.Windows.Capture.CaptureOptions(camera.Loop,
            options.Capture.FallbackFps, TimeSpan.FromMilliseconds(options.Capture.ReconnectInitialMs),
            TimeSpan.FromMilliseconds(options.Capture.ReconnectMaximumMs), options.Capture.OpenTimeoutMs,
            options.Capture.ReadTimeoutMs);
        if (int.TryParse(camera.Source, out var index)) return new WebcamVideoSource(index, capture);
        if (camera.Source.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            return new RtspVideoSource(CameraSource.WithCredentials(camera.Source, credentials), capture);
        return new FileVideoSource(camera.Source, capture);
    }

    private CameraView View(CameraDefinition camera)
    {
        var status = metrics.Cameras().FirstOrDefault(x => x.CameraId == camera.Id);
        return new(camera.Id, camera.Name, camera.Source, camera.Enabled, _sessions.ContainsKey(camera.Id),
            status?.Online ?? false, status?.FramesPerSecond ?? 0, status?.Error);
    }

    private static (CameraDefinition Camera, CameraCredentials? Credentials) Prepare(CameraWriteRequest request, string id, IReadOnlyList<NormalizedZone> zones)
    {
        var (source, credentials) = CameraSource.Sanitize(request.Source, request.Username, request.Password);
        var camera = new CameraDefinition(id, request.Name.Trim(), source, request.Enabled, request.Loop, zones);
        JsonCameraStore.Validate(camera);
        return (camera, credentials);
    }

    private IReadOnlyList<CameraDefinition> SeedCameras() => options.Cameras.Select(camera =>
    {
        var zones = camera.Zones.Select(zone => new NormalizedZone(zone.Name,
            zone.Points.Select(point => new NormalizedPoint(point.X, point.Y)).ToArray())).ToArray();
        if (zones.Length > 0 && !string.Equals(camera.ZonesCoordinateSpace, "normalized", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Las zonas sembradas de '{camera.Id}' deben usar coordenadas normalizadas.");
        var (source, _) = CameraSource.Sanitize(camera.Source, null, null);
        return new CameraDefinition(camera.Id, camera.Name, source, camera.Enabled, camera.Loop, zones);
    }).ToArray();

    private Task Ready(CancellationToken token) => _ready.Task.WaitAsync(token);
    private ModelDescriptor Model() { var path = Environment.ExpandEnvironmentVariables(options.Model.Path); path = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path)); return new(path, options.Model.InputWidth, options.Model.InputHeight, options.Model.ClassCount, options.Model.KeypointCount, options.Model.ConfidenceThreshold, options.Model.IouThreshold, options.Model.MaximumDetections); }
    private DetectionOptions Detection() => new() { KeypointConfidence = options.Hands.KeypointConfidence, ShoulderMarginRatio = options.Hands.ShoulderMarginRatio, StrictMode = options.Hands.StrictMode, StrictMarginPixels = options.Hands.StrictMarginPixels, ConsecutiveFrames = options.Hands.ConsecutiveFrames, LowerConsecutiveFrames = options.Hands.LowerConsecutiveFrames, Cooldown = TimeSpan.FromMilliseconds(options.Hands.CooldownMs), TrackTimeToLive = TimeSpan.FromMilliseconds(options.Hands.TrackTimeToLiveMs) };
    private HandRaise.Application.Tracking.TrackerOptions Tracker() => new() { HighConfidenceThreshold = options.Tracker.HighConfidenceThreshold, LowConfidenceThreshold = options.Tracker.LowConfidenceThreshold, NewTrackThreshold = options.Tracker.NewTrackThreshold, FirstMatchIouThreshold = options.Tracker.FirstMatchIouThreshold, SecondMatchIouThreshold = options.Tracker.SecondMatchIouThreshold, LostTrackBufferFrames = options.Tracker.LostTrackBufferFrames, NominalFramesPerSecond = options.Tracker.NominalFramesPerSecond, PositionProcessNoise = options.Tracker.PositionProcessNoise, VelocityProcessNoise = options.Tracker.VelocityProcessNoise, MeasurementNoise = options.Tracker.MeasurementNoise };
    private StorageWorkerOptions StorageWorkerOptions() => new(options.Storage.QueueCapacity, options.Storage.BatchSize, options.Storage.SnapshotDirectory, options.Storage.RetentionDays, options.Storage.MaximumSnapshotMegabytes is { } value ? (long)(value * 1024 * 1024) : null, TimeSpan.FromMinutes(options.Storage.CleanupIntervalMinutes));
    private static async Task<SwitchableInferenceBackend> LoadBackendAsync(DeviceInfo preferred, IReadOnlyList<DeviceInfo> devices, ModelDescriptor model, WindowsMlBackendFactory factory, DevicePreferenceService preferences, CancellationToken token) { async Task<SwitchableInferenceBackend> Load(DeviceInfo device) { var value = new SwitchableInferenceBackend(factory.Create(device), factory); try { await value.LoadAsync(model, token); return value; } catch { await value.DisposeAsync(); throw; } } try { return await Load(preferred); } catch when (preferred.Backend != InferenceBackend.Cpu) { var cpu = devices.First(device => device.Backend == InferenceBackend.Cpu); await preferences.SelectAsync(devices, cpu.Id, token); return await Load(cpu); } }

    private sealed class StreamSubscriber(
        Channel<byte[]> channel,
        double fps,
        int quality,
        Action onDispose) : IAsyncDisposable
    {
        private bool _disposed;
        public Channel<byte[]> Channel { get; } = channel;
        public double Fps { get; } = fps;
        public int Quality { get; } = quality;
        public long IntervalTicks { get; } = (long)(Stopwatch.Frequency / Math.Max(1.0, fps));
        public long LastSentTicks { get; set; }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                onDispose();
                Channel.Writer.TryComplete();
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CameraSession(CancellationTokenSource cancellation, AtomicZoneProvider zones) : IDisposable
    {
        private readonly object _subscribersLock = new();
        private readonly List<StreamSubscriber> _subscribers = [];
        private byte[]? _snapshot;

        public CancellationTokenSource Cancellation { get; } = cancellation;
        public AtomicZoneProvider Zones { get; } = zones;
        public Task? Task { get; set; }

        public void SetSnapshot(byte[] value) => Volatile.Write(ref _snapshot, value);
        public byte[]? GetSnapshot() => Volatile.Read(ref _snapshot)?.ToArray();

        public void OnFrame(VideoFrame frame, int snapshotQuality)
        {
            SetSnapshot(OpenCvFrameJpegEncoder.Encode(frame, snapshotQuality));

            StreamSubscriber[] subscribers;
            lock (_subscribersLock)
            {
                if (_subscribers.Count == 0) return;
                subscribers = _subscribers.ToArray();
            }

            var nowTicks = Stopwatch.GetTimestamp();
            var eligible = subscribers.Where(s => nowTicks - s.LastSentTicks >= s.IntervalTicks).ToArray();
            if (eligible.Length == 0) return;

            var qualityGroups = eligible.GroupBy(s => s.Quality);
            foreach (var group in qualityGroups)
            {
                byte[]? jpeg = null;
                try
                {
                    jpeg = OpenCvFrameJpegEncoder.Encode(frame, group.Key);
                }
                catch
                {
                    continue;
                }

                foreach (var subscriber in group)
                {
                    subscriber.LastSentTicks = nowTicks;
                    subscriber.Channel.Writer.TryWrite(jpeg);
                }
            }
        }

        public (CameraStreamStatus Status, StreamSubscriber? Subscriber, string? Error) SubscribeStream(
            int maxClients, double fps, int quality)
        {
            lock (_subscribersLock)
            {
                if (Cancellation.IsCancellationRequested)
                    return (CameraStreamStatus.Offline, null, "La cámara no está activa.");

                if (_subscribers.Count >= maxClients)
                    return (CameraStreamStatus.LimitReached, null, $"Límite de {maxClients} clientes de streaming alcanzado.");

                var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleWriter = true,
                    SingleReader = true
                });

                StreamSubscriber? subscriber = null;
                subscriber = new StreamSubscriber(channel, fps, quality, () =>
                {
                    lock (_subscribersLock)
                    {
                        _subscribers.Remove(subscriber!);
                    }
                });

                _subscribers.Add(subscriber);
                return (CameraStreamStatus.Success, subscriber, null);
            }
        }

        public void Dispose()
        {
            Cancellation.Dispose();
            lock (_subscribersLock)
            {
                foreach (var sub in _subscribers)
                {
                    sub.Channel.Writer.TryComplete();
                }
                _subscribers.Clear();
            }
        }
    }
}

internal static class CameraSource
{
    public static (string Source, CameraCredentials? Credentials) Sanitize(string source, string? username, string? password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (!source.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password)) throw new ArgumentException("Las credenciales solo son válidas para RTSP.");
            return (source.Trim(), null);
        }
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) throw new ArgumentException("La fuente RTSP no es válida.");
        var parts = uri.UserInfo.Split(':', 2);
        var embeddedUser = parts.Length > 0 ? Uri.UnescapeDataString(parts[0]) : string.Empty;
        var embeddedPassword = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        var credentials = !string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password)
            ? new CameraCredentials(username ?? string.Empty, password ?? string.Empty)
            : string.IsNullOrEmpty(uri.UserInfo) ? null : new CameraCredentials(embeddedUser, embeddedPassword);
        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return (builder.Uri.AbsoluteUri, credentials);
    }

    public static string WithCredentials(string source, CameraCredentials? credentials)
    {
        if (credentials is null) return source;
        var builder = new UriBuilder(source) { UserName = credentials.Username, Password = credentials.Password };
        return builder.Uri.AbsoluteUri;
    }
}
