using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using HandRaise.Application.Capture;
using HandRaise.Application.Hardware;
using HandRaise.Application.Inference;
using HandRaise.Application.Events;
using HandRaise.Application.Pipelines;
using HandRaise.Application.Tracking;
using HandRaise.Application.Storage;
using HandRaise.Infrastructure.Windows.Capture;
using HandRaise.Infrastructure.Windows.Inference;
using HandRaise.Infrastructure.Windows.Overlay;
using HandRaise.Infrastructure.Windows.Settings;
using HandRaise.Infrastructure.Windows.Storage;
using OpenCvSharp;

namespace HandRaise.DebugApp;

internal sealed class DebugRunner(
    DebugConfiguration configuration,
    DebugAppOptions options,
    IReadOnlyList<DeviceInfo> devices)
{
    private readonly WindowsMlBackendFactory _backendFactory = new();

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        if (options.ListEvents)
        {
            return await ListEventsAsync(cancellationToken);
        }

        if (options.Benchmark)
        {
            return await RunBenchmarkAsync(cancellationToken);
        }

        var store = new JsonUserSettingsStore();
        var preferences = new DevicePreferenceService(store);
        var initial = await ResolveInitialDeviceAsync(preferences, cancellationToken);
        await using var switchable = new SwitchableInferenceBackend(
            _backendFactory.Create(initial), _backendFactory);
        await switchable.LoadAsync(configuration.CreateModel(), cancellationToken);

        await using var source = CreateSource();
        await using var rawSink = CreateRawSink(source);
        await using var annotatedSink = CreateAnnotatedSink(source);
        await using var eventBus = new HandEventBus();
        EventPersistenceWorker? persistence = null;
        if (options.Persist)
        {
            try
            {
                var repository = await CreateRepositoryAsync(cancellationToken);
                persistence = new EventPersistenceWorker(
                    eventBus,
                    repository,
                    new SnapshotStore(configuration.Storage.SnapshotDirectory),
                    configuration.CreateStorageWorkerOptions(),
                    log: message => Console.Error.WriteLine($"ADVERTENCIA: {message}"));
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"ADVERTENCIA: persistencia deshabilitada; el pipeline continúa: {exception.Message}");
            }
        }

        await using var consoleEvents = eventBus.Subscribe();
        var consoleTask = WriteConsoleEventsAsync(consoleEvents.Reader);
        HandEventSubscription? jsonEvents = null;
        Task jsonTask = Task.CompletedTask;
        if (options.EventsJsonlPath is not null)
        {
            jsonEvents = eventBus.Subscribe();
            jsonTask = WriteJsonLinesAsync(jsonEvents.Reader, options.EventsJsonlPath);
        }

        var pipeline = CreatePipeline(
            source,
            switchable,
            rawSink,
            eventBus,
            () => switchable.Generation,
            persistence is not null);
        var metrics = new MetricsAccumulator();
        using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (options.MaximumSeconds is { } seconds)
        {
            duration.CancelAfter(TimeSpan.FromSeconds(seconds));
        }

        try
        {
            await foreach (var result in pipeline.RunAsync(duration.Token))
            {
                using (result)
                {
                    metrics.Add(result.Metrics);
                    if (annotatedSink is not null)
                    {
                        await annotatedSink.WriteAsync(result.Frame, duration.Token);
                    }

                    if (!options.NoWindow)
                    {
                        Show(result.Frame);
                        var key = Cv2.WaitKey(1) & 0xff;
                        if (key == 'q')
                        {
                            duration.Cancel();
                            break;
                        }

                        if (key == 'd')
                        {
                            var next = NextDevice(switchable.Device);
                            var changed = await switchable.SwitchAsync(next, duration.Token);
                            Console.WriteLine(changed.Success
                                ? $"Dispositivo activo: {changed.ActiveDevice.Name}; cambio {changed.Duration.TotalMilliseconds:0.0} ms."
                                : $"Cambio rechazado; continúa {changed.ActiveDevice.Name}: {changed.Error}");
                            if (changed.Success)
                            {
                                await preferences.SelectAsync(devices, changed.ActiveDevice.Id, duration.Token);
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (duration.IsCancellationRequested)
        {
        }
        finally
        {
            if (!options.NoWindow)
            {
                Cv2.DestroyAllWindows();
            }

            await eventBus.DisposeAsync();
            if (persistence is not null)
            {
                await persistence.DisposeAsync();
            }
            await Task.WhenAll(consoleTask, jsonTask);
            if (jsonEvents is not null)
            {
                await jsonEvents.DisposeAsync();
            }
        }

        var summary = metrics.Summarize(switchable.ExecutionInfo);
        PrintSummary(summary);
        await SaveJsonAsync(summary, cancellationToken);
        return 0;
    }

    private async Task<int> RunBenchmarkAsync(CancellationToken cancellationToken)
    {
        if (options.MaximumSeconds is null)
        {
            throw new ArgumentException("--benchmark requiere --max-seconds.");
        }

        var benchmarkDevices = devices
            .Where(device => device.RuntimeAvailable &&
                device.Backend is InferenceBackend.Cpu or InferenceBackend.DirectMl)
            .ToArray();
        var summaries = new List<MetricsSummary>();
        foreach (var device in benchmarkDevices)
        {
            await using var backend = _backendFactory.Create(device);
            await backend.LoadAsync(configuration.CreateModel(), cancellationToken);
            await using var source = CreateSource();
            var pipeline = CreatePipeline(source, backend, null);
            var accumulator = new MetricsAccumulator();
            using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            duration.CancelAfter(TimeSpan.FromSeconds(options.MaximumSeconds.Value));
            try
            {
                await foreach (var result in pipeline.RunAsync(duration.Token))
                {
                    using (result)
                    {
                        accumulator.Add(result.Metrics);
                    }
                }
            }
            catch (OperationCanceledException) when (duration.IsCancellationRequested)
            {
            }

            summaries.Add(accumulator.Summarize(backend.ExecutionInfo));
        }

        PrintBenchmark(summaries);
        var warning = DetectSilentFallbackWarning(summaries);
        if (warning is not null)
        {
            Console.WriteLine($"ADVERTENCIA: {warning}");
        }

        await SaveJsonAsync(new { results = summaries, warning }, cancellationToken);
        return summaries.Count == 0 ? 2 : 0;
    }

    private CameraPipeline CreatePipeline(
        IVideoSource source,
        IInferenceBackend backend,
        IVideoFrameSink? rawSink,
        IHandEventPublisher? eventPublisher = null,
        Func<long>? backendGeneration = null,
        bool persistSnapshots = false) => new(
            "debug-camera",
            source,
            backend,
            new OpenCvOverlayRenderer(configuration.Hands.KeypointConfidence),
            configuration.CreateDetectionOptions(),
            configuration.Zones,
            configuration.Model.Name,
            new ByteTracker(configuration.CreateTrackerOptions()),
            rawSink,
            eventPublisher,
            backendGeneration,
            persistSnapshots ? new OpenCvPersonSnapshotEncoder() : null,
            persistSnapshots ? configuration.CreateSnapshotEncodingOptions() : null,
            message => Console.Error.WriteLine($"ADVERTENCIA: {message}"));

    private async Task<SqliteHandEventRepository> CreateRepositoryAsync(
        CancellationToken cancellationToken)
    {
        var databaseOptions = StorageDatabase.CreateOptions(configuration.Storage.DatabasePath);
        await StorageDatabase.MigrateAsync(databaseOptions, cancellationToken);
        return new SqliteHandEventRepository(databaseOptions);
    }

    private async Task<int> ListEventsAsync(CancellationToken cancellationToken)
    {
        var repository = await CreateRepositoryAsync(cancellationToken);
        var events = await repository.QueryAsync(
            new EventQuery(
                CameraId: options.CameraFilter,
                From: options.From,
                To: options.To,
                Limit: 1000),
            cancellationToken);
        foreach (var handEvent in events)
        {
            Console.WriteLine(JsonSerializer.Serialize(handEvent));
        }

        return 0;
    }

    private static async Task WriteConsoleEventsAsync(
        System.Threading.Channels.ChannelReader<HandEvent> reader)
    {
        await foreach (var handEvent in reader.ReadAllAsync())
        {
            Console.WriteLine(
                $"{handEvent.Timestamp:O} {handEvent.CameraId} track={handEvent.TrackId} " +
                $"{handEvent.Type} mano={handEvent.Hand} zona={handEvent.Zone ?? "-"}");
        }
    }

    private static async Task WriteJsonLinesAsync(
        System.Threading.Channels.ChannelReader<HandEvent> reader,
        string outputPath)
    {
        var path = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        await using var writer = new StreamWriter(stream);
        await foreach (var handEvent in reader.ReadAllAsync())
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(handEvent));
        }
    }

    private IVideoSource CreateSource()
    {
        var capture = configuration.CreateCaptureOptions();
        Action<string> log = message => Console.WriteLine($"[{Stopwatch.GetTimestamp()}] {message}");
        if (int.TryParse(options.Source, out var cameraIndex))
        {
            return new WebcamVideoSource(cameraIndex, capture, log);
        }

        if (options.Source!.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
        {
            return new RtspVideoSource(options.Source, capture, log);
        }

        return new FileVideoSource(options.Source, capture);
    }

    private IVideoFrameSink? CreateRawSink(IVideoSource source)
    {
        if (options.RecordRawDirectory is null)
        {
            return null;
        }

        var directory = Path.GetFullPath(options.RecordRawDirectory);
        Directory.CreateDirectory(directory);
        return new OpenCvVideoWriterSink(
            Path.Combine(directory, "session-raw.mp4"),
            () => EffectiveFramesPerSecond(source));
    }

    private IVideoFrameSink? CreateAnnotatedSink(IVideoSource source) => options.RecordPath is null
        ? null
        : new OpenCvVideoWriterSink(options.RecordPath, () => EffectiveFramesPerSecond(source));

    private double EffectiveFramesPerSecond(IVideoSource source) => source.FramesPerSecond > 0
        ? source.FramesPerSecond
        : configuration.Capture.FallbackFps;

    private async Task<DeviceInfo> ResolveInitialDeviceAsync(
        DevicePreferenceService preferences,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.DeviceId))
        {
            return await preferences.SelectAsync(devices, options.DeviceId, cancellationToken);
        }

        return (await preferences.InitializeAsync(devices, cancellationToken)).Device;
    }

    private DeviceInfo NextDevice(DeviceInfo active)
    {
        var available = devices.Where(device => device.RuntimeAvailable &&
            device.Backend is InferenceBackend.Cpu or InferenceBackend.DirectMl).ToArray();
        var index = Array.FindIndex(available, device => device.Id == active.Id);
        return available[(index + 1 + available.Length) % available.Length];
    }

    private static void Show(VideoFrame frame)
    {
        using var mat = new Mat(frame.Height, frame.Width, MatType.CV_8UC3);
        var pixels = frame.Pixels.ToArray();
        Marshal.Copy(pixels, 0, mat.Data, pixels.Length);
        Cv2.ImShow("Hand Raise Detection", mat);
    }

    private static void PrintSummary(MetricsSummary summary)
    {
        Console.WriteLine(
            $"Frames={summary.Frames}, FPS={summary.FramesPerSecond:0.00}, " +
            $"inferencia media={summary.InferenceMeanMs:0.00} ms, p95={summary.InferenceP95Ms:0.00} ms.");
        Console.WriteLine(
            $"Provider={summary.Execution.ProviderName}, dispositivo={summary.Execution.DeviceName}, " +
            $"verificación={summary.Execution.Verification}");
    }

    private static void PrintBenchmark(IEnumerable<MetricsSummary> summaries)
    {
        Console.WriteLine("Dispositivo".PadRight(30) + "Etapa".PadRight(14) +
            "Media ms".PadLeft(12) + "P95 ms".PadLeft(12) + "FPS".PadLeft(9));
        foreach (var item in summaries)
        {
            var stages = new (string Name, double Mean, double P95)[]
            {
                ("edad frame", item.FrameAgeMeanMs, item.FrameAgeP95Ms),
                ("preproceso", item.PreprocessMeanMs, item.PreprocessP95Ms),
                ("inferencia", item.InferenceMeanMs, item.InferenceP95Ms),
                ("postproceso", item.PostprocessMeanMs, item.PostprocessP95Ms),
                ("overlay", item.OverlayMeanMs, item.OverlayP95Ms),
                ("total", item.TotalMeanMs, item.TotalP95Ms)
            };
            foreach (var (stage, mean, p95) in stages)
            {
                Console.WriteLine(item.Execution.DeviceName.PadRight(30) +
                    stage.PadRight(14) +
                    mean.ToString("0.00").PadLeft(12) +
                    p95.ToString("0.00").PadLeft(12) +
                    item.FramesPerSecond.ToString("0.00").PadLeft(9));
            }

            Console.WriteLine($"  Provider: {item.Execution.ProviderName}");
            Console.WriteLine($"  Confirmación: {item.Execution.Verification}");
        }
    }

    private string? DetectSilentFallbackWarning(IReadOnlyList<MetricsSummary> summaries)
    {
        var cpu = summaries.FirstOrDefault(item => item.Execution.ProviderName.Contains("CPU"));
        if (cpu is null || cpu.InferenceMeanMs <= 0)
        {
            return null;
        }

        var suspicious = summaries.Where(item => item.Execution.ProviderName.Contains("Dml", StringComparison.OrdinalIgnoreCase))
            .Where(item => Math.Abs(item.InferenceMeanMs - cpu.InferenceMeanMs) / cpu.InferenceMeanMs <=
                configuration.Benchmark.IndistinguishableLatencyRatio)
            .Select(item => item.Execution.DeviceName)
            .ToArray();
        return suspicious.Length == 0
            ? null
            : $"Las latencias de CPU y {string.Join(", ", suspicious)} son indistinguibles; " +
              "podría existir fallback silencioso a CPU aunque el EpDevice fue asignado explícitamente.";
    }

    private async Task SaveJsonAsync(object value, CancellationToken cancellationToken)
    {
        if (options.MetricsJsonPath is null)
        {
            return;
        }

        var path = Path.GetFullPath(options.MetricsJsonPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(value, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            }),
            cancellationToken);
    }
}
