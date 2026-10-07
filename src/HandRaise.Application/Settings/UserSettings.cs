namespace HandRaise.Application.Settings;

public sealed record UserSettings(
    string? DeviceId = null,
    string? AccelerationMode = null,
    string? PerformanceProfile = null,
    int? TargetInferenceFps = null,
    int? PreviewFps = null);
