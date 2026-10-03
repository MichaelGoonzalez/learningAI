using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using HandRaise.Application.Inference;
using HandRaise.Application.Storage;
using HandRaise.Application.Tracking;
using HandRaise.Domain.Detection;
using HandRaise.Domain.Zones;
using HandRaise.Infrastructure.Windows.Capture;
using HandRaise.Infrastructure.Windows.Storage;

namespace HandRaise.Desktop.Configuration;

public sealed record DesktopConfiguration(
    ModelSettings Model,
    CaptureSettings Capture,
    HandSettings Hands,
    TrackerSettings Tracker,
    StorageSettings Storage,
    IReadOnlyList<CameraSettings> Cameras,
    UiSettings Ui)
{
    public static DesktopConfiguration Load()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.Combine(Environment.CurrentDirectory, "src", "HandRaise.Desktop", "appsettings.json")
        };
        var path = candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("No se encontró appsettings.json de HandRaise.Desktop.");
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            NumberHandling = JsonNumberHandling.Strict
        };
        var configuration = JsonSerializer.Deserialize<DesktopConfiguration>(
            File.ReadAllText(path), options)
            ?? throw new InvalidDataException("La configuración de escritorio no es válida.");
        if (configuration.Cameras.Select(camera => camera.Id).Distinct().Count() !=
            configuration.Cameras.Count)
        {
            throw new InvalidDataException("Los IDs de cámara deben ser únicos.");
        }

        foreach (var camera in configuration.Cameras)
        {
            foreach (var zone in camera.Zones)
            {
                zone.Validate();
            }
        }

        return configuration;
    }

    public ModelDescriptor CreateModel() => new(
        ResolvePath(Model.Path),
        Model.InputWidth,
        Model.InputHeight,
        Model.ClassCount,
        Model.KeypointCount,
        Model.ConfidenceThreshold,
        Model.IouThreshold,
        Model.MaximumDetections);

    public CaptureOptions CreateCaptureOptions(bool loop) => new(
        loop,
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

    private static string ResolvePath(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        return Path.GetFullPath(Path.IsPathRooted(expanded)
            ? expanded
            : Path.Combine(AppContext.BaseDirectory, expanded));
    }
}

public sealed record CameraSettings(
    string Id,
    string Name,
    string Source,
    bool Enabled,
    bool Loop,
    IReadOnlyList<ZoneDefinition> Zones,
    string ZonesCoordinateSpace = "pixels");

public sealed record ModelSettings(
    string Path,
    string Name,
    int InputWidth,
    int InputHeight,
    int ClassCount,
    int KeypointCount,
    float ConfidenceThreshold,
    float IouThreshold,
    int MaximumDetections);

public sealed record CaptureSettings(
    double FallbackFps,
    double ReconnectInitialMs,
    double ReconnectMaximumMs,
    int OpenTimeoutMs,
    int ReadTimeoutMs);

public sealed record HandSettings(
    double KeypointConfidence,
    double ShoulderMarginRatio,
    bool StrictMode,
    double StrictMarginPixels,
    int ConsecutiveFrames,
    int LowerConsecutiveFrames,
    double CooldownMs,
    double TrackTimeToLiveMs);

public sealed record TrackerSettings(
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

public sealed record StorageSettings(
    string DatabasePath,
    string SnapshotDirectory,
    int QueueCapacity,
    int BatchSize,
    double SnapshotMarginRatio,
    int JpegQuality,
    int? RetentionDays,
    double? MaximumSnapshotMegabytes,
    double CleanupIntervalMinutes);

public sealed record UiSettings(bool DarkTheme, int HistoryLimit);
