using System.Text.Json;
using System.Text.Json.Serialization;
using HandRaise.Application.Inference;
using HandRaise.Domain.Detection;
using HandRaise.Domain.Zones;
using HandRaise.Infrastructure.Windows.Capture;
using HandRaise.Application.Tracking;
using HandRaise.Application.Storage;
using HandRaise.Infrastructure.Windows.Storage;

namespace HandRaise.DebugApp;

internal sealed record DebugConfiguration(
    ModelSettings Model,
    CaptureSettings Capture,
    HandSettings Hands,
    TrackerSettings Tracker,
    StorageSettings Storage,
    BenchmarkSettings Benchmark,
    IReadOnlyList<ZoneDefinition> Zones)
{
    public static DebugConfiguration Load()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(Environment.CurrentDirectory, "src", "HandRaise.DebugApp", "appsettings.json")
        };
        var path = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("No se encontró appsettings.json de HandRaise.DebugApp.");
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            NumberHandling = JsonNumberHandling.Strict
        };
        return JsonSerializer.Deserialize<DebugConfiguration>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("appsettings.json no contiene configuración válida.");
    }

    public ModelDescriptor CreateModel() => new(
        Path.GetFullPath(Model.Path),
        Model.InputWidth,
        Model.InputHeight,
        Model.ClassCount,
        Model.KeypointCount,
        Model.ConfidenceThreshold,
        Model.IouThreshold,
        Model.MaximumDetections);

    public CaptureOptions CreateCaptureOptions() => new(
        Capture.Loop,
        Capture.FallbackFps,
        TimeSpan.FromMilliseconds(Capture.ReconnectInitialMs),
        TimeSpan.FromMilliseconds(Capture.ReconnectMaximumMs),
        Capture.OpenTimeoutMs,
        Capture.ReadTimeoutMs);

    public DetectionOptions CreateDetectionOptions() => new()
    {
        KeypointConfidence = Hands.KeypointConfidence,
        ShoulderMarginRatio = Hands.ShoulderMarginRatio,
        StrictMode = Hands.StrictMode,
        StrictMarginPixels = Hands.StrictMarginPixels,
        ConsecutiveFrames = Hands.ConsecutiveFrames,
        LowerConsecutiveFrames = Hands.LowerConsecutiveFrames,
        Cooldown = TimeSpan.FromMilliseconds(Hands.CooldownMs),
        TrackTimeToLive = TimeSpan.FromMilliseconds(Hands.TrackTimeToLiveMs)
    };

    public TrackerOptions CreateTrackerOptions() => new()
    {
        HighConfidenceThreshold = Tracker.HighConfidenceThreshold,
        LowConfidenceThreshold = Tracker.LowConfidenceThreshold,
        NewTrackThreshold = Tracker.NewTrackThreshold,
        FirstMatchIouThreshold = Tracker.FirstMatchIouThreshold,
        SecondMatchIouThreshold = Tracker.SecondMatchIouThreshold,
        LostTrackBufferFrames = Tracker.LostTrackBufferFrames,
        NominalFramesPerSecond = Tracker.NominalFramesPerSecond,
        PositionProcessNoise = Tracker.PositionProcessNoise,
        VelocityProcessNoise = Tracker.VelocityProcessNoise,
        MeasurementNoise = Tracker.MeasurementNoise
    };

    public StorageWorkerOptions CreateStorageWorkerOptions() => new(
        Storage.QueueCapacity,
        Storage.BatchSize,
        Storage.SnapshotDirectory,
        Storage.RetentionDays,
        Storage.MaximumSnapshotMegabytes is { } megabytes
            ? checked((long)(megabytes * 1024 * 1024))
            : null,
        TimeSpan.FromMinutes(Storage.CleanupIntervalMinutes));

    public SnapshotEncodingOptions CreateSnapshotEncodingOptions() => new(
        Storage.SnapshotMarginRatio,
        Storage.JpegQuality);
}

internal sealed record ModelSettings(
    string Path,
    string Name,
    int InputWidth,
    int InputHeight,
    int ClassCount,
    int KeypointCount,
    float ConfidenceThreshold,
    float IouThreshold,
    int MaximumDetections);

internal sealed record CaptureSettings(
    bool Loop,
    double FallbackFps,
    double ReconnectInitialMs,
    double ReconnectMaximumMs,
    int OpenTimeoutMs,
    int ReadTimeoutMs);

internal sealed record HandSettings(
    double KeypointConfidence,
    double ShoulderMarginRatio,
    bool StrictMode,
    double StrictMarginPixels,
    int ConsecutiveFrames,
    int LowerConsecutiveFrames,
    double CooldownMs,
    double TrackTimeToLiveMs);

internal sealed record TrackerSettings(
    float HighConfidenceThreshold,
    float LowConfidenceThreshold,
    float NewTrackThreshold,
    float FirstMatchIouThreshold,
    float SecondMatchIouThreshold,
    int LostTrackBufferFrames,
    double NominalFramesPerSecond,
    double PositionProcessNoise,
    double VelocityProcessNoise,
    double MeasurementNoise);

internal sealed record StorageSettings(
    string DatabasePath,
    string SnapshotDirectory,
    int QueueCapacity,
    int BatchSize,
    double SnapshotMarginRatio,
    int JpegQuality,
    int? RetentionDays,
    double? MaximumSnapshotMegabytes,
    double CleanupIntervalMinutes);

internal sealed record BenchmarkSettings(double IndistinguishableLatencyRatio);
