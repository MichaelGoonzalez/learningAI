namespace HandRaise.Host.Configuration;

public sealed class HostOptions
{
    public string NodeId { get; init; } = "local-node";
    public string SiteId { get; init; } = "default-site";
    public ApiOptions Api { get; init; } = new();
    public CapacityOptions Capacity { get; init; } = new();
    public ModelOptions Model { get; init; } = new();
    public CaptureOptions Capture { get; init; } = new();
    public HandOptions Hands { get; init; } = new();
    public TrackerOptions Tracker { get; init; } = new();
    public StorageOptions Storage { get; init; } = new();
    public StreamingOptions Streaming { get; init; } = new();
    public List<CameraOptions> Cameras { get; init; } = [];
}

public sealed class StreamingOptions
{
    public double MetricsIntervalSeconds { get; init; } = 2.0;
    public int MaxStreamClientsPerCamera { get; init; } = 4;
    public double DefaultStreamFps { get; init; } = 15.0;
    public int DefaultStreamQuality { get; init; } = 80;
    public int EventChannelCapacity { get; init; } = 128;
}

public sealed class CapacityOptions
{
    public double TargetFramesPerSecond { get; init; } = 5;
}

public sealed class ApiOptions
{
    public int Port { get; init; } = 5080;
    public string BindAddress { get; init; } = "127.0.0.1";
    public string ApiKeyHeader { get; init; } = "X-Api-Key";
    public string? ApiKey { get; init; }
    public string[] AllowedOrigins { get; init; } = [];
}

public sealed class ModelOptions
{
    public string Path { get; init; } = "models/yolo26n-pose.onnx";
    public string Name { get; init; } = "YOLO26n Pose";
    public int InputWidth { get; init; } = 640;
    public int InputHeight { get; init; } = 640;
    public int ClassCount { get; init; } = 1;
    public int KeypointCount { get; init; } = 17;
    public float ConfidenceThreshold { get; init; } = 0.25f;
    public float IouThreshold { get; init; } = 0.7f;
    public int MaximumDetections { get; init; } = 300;
}

public sealed class CaptureOptions
{
    public double FallbackFps { get; init; } = 30;
    public int ReconnectInitialMs { get; init; } = 500;
    public int ReconnectMaximumMs { get; init; } = 10_000;
    public int OpenTimeoutMs { get; init; } = 5_000;
    public int ReadTimeoutMs { get; init; } = 3_000;
}

public sealed class HandOptions
{
    public double KeypointConfidence { get; init; } = 0.5;
    public double ShoulderMarginRatio { get; init; } = 0.15;
    public bool StrictMode { get; init; }
    public double StrictMarginPixels { get; init; }
    public int ConsecutiveFrames { get; init; } = 3;
    public int LowerConsecutiveFrames { get; init; } = 3;
    public int CooldownMs { get; init; } = 1_000;
    public int TrackTimeToLiveMs { get; init; } = 5_000;
}

public sealed class TrackerOptions
{
    public float HighConfidenceThreshold { get; init; } = 0.5f;
    public float LowConfidenceThreshold { get; init; } = 0.1f;
    public float NewTrackThreshold { get; init; } = 0.5f;
    public float FirstMatchIouThreshold { get; init; } = 0.3f;
    public float SecondMatchIouThreshold { get; init; } = 0.2f;
    public int LostTrackBufferFrames { get; init; } = 30;
    public double NominalFramesPerSecond { get; init; } = 30;
    public double PositionProcessNoise { get; init; } = 1;
    public double VelocityProcessNoise { get; init; } = 0.1;
    public double MeasurementNoise { get; init; } = 10;
}

public sealed class StorageOptions
{
    public string DatabasePath { get; init; } = "%LOCALAPPDATA%/HandRaiseDetection/events.db";
    public string SnapshotDirectory { get; init; } = "%LOCALAPPDATA%/HandRaiseDetection/snapshots";
    public string CameraStorePath { get; init; } = "%LOCALAPPDATA%/HandRaiseDetection/cameras.json";
    public string CameraCredentialStorePath { get; init; } = "%LOCALAPPDATA%/HandRaiseDetection/camera-credentials.json";
    public int QueueCapacity { get; init; } = 256;
    public int BatchSize { get; init; } = 32;
    public double SnapshotMarginRatio { get; init; } = 0.1;
    public int JpegQuality { get; init; } = 88;
    public int? RetentionDays { get; init; } = 30;
    public double? MaximumSnapshotMegabytes { get; init; } = 1024;
    public int CleanupIntervalMinutes { get; init; } = 60;
}

public sealed class CameraOptions
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public bool Enabled { get; init; } = true;
    public bool Loop { get; init; }
    public List<ZoneOptions> Zones { get; init; } = [];
    public string ZonesCoordinateSpace { get; init; } = "pixels";
}

public sealed class ZoneOptions
{
    public string Name { get; init; } = string.Empty;
    public List<PointOptions> Points { get; init; } = [];
}

public sealed class PointOptions
{
    public double X { get; init; }
    public double Y { get; init; }
}
