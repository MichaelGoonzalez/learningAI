namespace HandRaise.Host.Services;

public enum NodeHealthStatus
{
    Healthy,
    Degraded,
    Failed
}

public sealed record PreflightCheckResult(
    string Name,
    NodeHealthStatus Status,
    string Message,
    bool IsCritical);

public sealed record NodeReadinessReport(
    string Status,
    bool Ready,
    string NodeId,
    string SiteId,
    double UptimeSeconds,
    IReadOnlyList<PreflightCheckResult> Checks);

public sealed record NodeDiagnosticsReport(
    string Status,
    string Version,
    string NodeId,
    string SiteId,
    double UptimeSeconds,
    string ActiveDeviceId,
    string ActiveDeviceName,
    string InferenceBackend,
    long WorkingSetBytes,
    int CameraCount,
    int OnlineCameraCount,
    IReadOnlyList<PreflightCheckResult> Checks,
    IReadOnlyList<string> RecentErrors);

