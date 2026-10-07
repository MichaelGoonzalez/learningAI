using System.Collections.Concurrent;

namespace HandRaise.Application.Pipelines;

public sealed record CameraRuntimeStatus(
    string CameraId,
    string Name,
    bool Online,
    double FramesPerSecond,
    long FramesProcessed,
    long FramesDropped,
    double LatencyMilliseconds,
    double AverageDecodeMilliseconds,
    double AverageInferenceMilliseconds,
    string? Error,
    double InferenceFps = 0.0,
    long DroppedInferenceFrames = 0,
    int QueueDepth = 0);

public sealed record PipelineMetricsSnapshot(
    string NodeId,
    string SiteId,
    double UptimeSeconds,
    IReadOnlyList<CameraRuntimeStatus> Cameras);

public sealed record CameraCapacity(
    string CameraId,
    double AverageDecodeMilliseconds,
    double AverageInferenceMilliseconds,
    int? EstimatedMaximumCameras);

public sealed record CapacitySnapshot(
    string NodeId,
    string SiteId,
    double TargetFramesPerSecond,
    int? EstimatedMaximumCameras,
    IReadOnlyList<CameraCapacity> Cameras);

public sealed class PipelineMetricsRegistry(
    string nodeId = "unknown",
    string siteId = "unknown")
{
    private readonly ConcurrentDictionary<string, Entry> _cameras = new(StringComparer.Ordinal);
    private readonly long _started = Environment.TickCount64;

    public void Register(string cameraId, string name) =>
        _cameras.TryAdd(cameraId, new Entry(cameraId, name));

    public void Update(string cameraId, CameraPipelineMetrics metrics) =>
        _cameras.GetOrAdd(cameraId, id => new Entry(id, id)).Update(metrics);

    public void SetOffline(string cameraId, string? error = null) =>
        _cameras.GetOrAdd(cameraId, id => new Entry(id, id)).SetOffline(error);

    public bool Remove(string cameraId) => _cameras.TryRemove(cameraId, out _);

    public IReadOnlyList<CameraRuntimeStatus> Cameras() => _cameras.Values
        .Select(entry => entry.Snapshot())
        .OrderBy(camera => camera.CameraId, StringComparer.Ordinal).ToArray();

    public PipelineMetricsSnapshot Snapshot() => new(
        nodeId,
        siteId,
        Math.Max(0, Environment.TickCount64 - _started) / 1000d,
        Cameras());

    public CapacitySnapshot Capacity(double targetFramesPerSecond)
    {
        if (!double.IsFinite(targetFramesPerSecond) || targetFramesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetFramesPerSecond));
        var cameras = Cameras().Select(camera =>
        {
            var cost = camera.AverageDecodeMilliseconds + camera.AverageInferenceMilliseconds;
            int? maximum = cost > 0
                ? Math.Max(1, (int)Math.Floor(1000 / (targetFramesPerSecond * cost)))
                : null;
            return new CameraCapacity(
                camera.CameraId,
                camera.AverageDecodeMilliseconds,
                camera.AverageInferenceMilliseconds,
                maximum);
        }).ToArray();
        var estimates = cameras.Where(camera => camera.EstimatedMaximumCameras.HasValue)
            .Select(camera => camera.EstimatedMaximumCameras!.Value).ToArray();
        return new CapacitySnapshot(
            nodeId,
            siteId,
            targetFramesPerSecond,
            estimates.Length == 0 ? null : estimates.Min(),
            cameras);
    }

    public HandRaise.Domain.Models.ExtendedCapacitySnapshot ExtendedCapacity(
        double targetFramesPerSecond,
        string deviceName = "D3D12 GPU",
        IReadOnlyList<HandRaise.Domain.Models.ModelDescriptor>? activeModels = null,
        IReadOnlyDictionary<string, int>? cameraAnalyticsCounts = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? cameraProviders = null)
    {
        if (!double.IsFinite(targetFramesPerSecond) || targetFramesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetFramesPerSecond));

        var cameraSnapshots = Cameras();
        var models = activeModels ?? [];
        var activeModelIds = models.Select(m => m.Id).ToList();
        var estimatedVram = models.Sum(m => m.MemoryEstimateMb);

        var cameraDetails = new List<HandRaise.Domain.Models.CameraCapacityDetail>();
        double aggFps = 0;
        double aggInferenceMs = 0;
        int totalAnalytics = 0;

        foreach (var cam in cameraSnapshots)
        {
            var analyticsCount = cameraAnalyticsCounts != null && cameraAnalyticsCounts.TryGetValue(cam.CameraId, out var count)
                ? count
                : 0;
            var providers = cameraProviders != null && cameraProviders.TryGetValue(cam.CameraId, out var prov)
                ? prov
                : (IReadOnlyList<string>)(activeModelIds.Count > 0 ? activeModelIds : ["default"]);

            totalAnalytics += analyticsCount;
            aggFps += cam.FramesPerSecond;
            aggInferenceMs += cam.AverageInferenceMilliseconds;

            var dropRate = (cam.FramesProcessed + cam.FramesDropped) > 0
                ? (double)cam.FramesDropped / (cam.FramesProcessed + cam.FramesDropped)
                : 0.0;

            var status = HandRaise.Domain.Models.CapacityStatus.Healthy;
            if (cam.Online)
            {
                if (cam.FramesPerSecond < targetFramesPerSecond * 0.6 || dropRate > 0.15 || cam.AverageInferenceMilliseconds > (1000.0 / targetFramesPerSecond))
                {
                    status = HandRaise.Domain.Models.CapacityStatus.OverCapacity;
                }
                else if (cam.FramesPerSecond < targetFramesPerSecond * 0.85 || dropRate > 0.05 || cam.AverageInferenceMilliseconds > (1000.0 / targetFramesPerSecond) * 0.75)
                {
                    status = HandRaise.Domain.Models.CapacityStatus.NearCapacity;
                }
            }

            cameraDetails.Add(new HandRaise.Domain.Models.CameraCapacityDetail(
                CameraId: cam.CameraId,
                TargetFps: targetFramesPerSecond,
                ActualFps: cam.FramesPerSecond,
                Providers: providers,
                AnalyticsCount: analyticsCount,
                InferenceMs: cam.AverageInferenceMilliseconds,
                DroppedFrames: cam.FramesDropped,
                Status: status));
        }

        var overallStatus = HandRaise.Domain.Models.CapacityStatus.Healthy;
        if (cameraDetails.Any(c => c.Status == HandRaise.Domain.Models.CapacityStatus.OverCapacity))
        {
            overallStatus = HandRaise.Domain.Models.CapacityStatus.OverCapacity;
        }
        else if (cameraDetails.Any(c => c.Status == HandRaise.Domain.Models.CapacityStatus.NearCapacity))
        {
            overallStatus = HandRaise.Domain.Models.CapacityStatus.NearCapacity;
        }

        var legacyEstimates = cameraSnapshots.Select(cam =>
        {
            var cost = cam.AverageDecodeMilliseconds + cam.AverageInferenceMilliseconds;
            return cost > 0
                ? Math.Max(1, (int)Math.Floor(1000 / (targetFramesPerSecond * cost)))
                : (int?)null;
        }).Where(m => m.HasValue).Select(m => m!.Value).ToArray();

        int? overallMaxCameras = legacyEstimates.Length == 0 ? null : legacyEstimates.Min();

        return new HandRaise.Domain.Models.ExtendedCapacitySnapshot(
            NodeId: nodeId,
            SiteId: siteId,
            Device: deviceName,
            ActiveModels: activeModelIds,
            EstimatedVramMb: estimatedVram,
            AggregateFps: aggFps,
            AggregateInferenceMs: aggInferenceMs,
            CameraCount: cameraSnapshots.Count,
            ActiveAnalyticCount: totalAnalytics,
            CapacityStatus: overallStatus,
            Cameras: cameraDetails,
            TargetFramesPerSecond: targetFramesPerSecond,
            EstimatedMaximumCameras: overallMaxCameras);
    }

    private sealed class Entry(string cameraId, string name)
    {
        private readonly object _sync = new();
        private CameraRuntimeStatus _status = new(cameraId, name, false, 0, 0, 0, 0, 0, 0, null);
        private long _samples;
        private double _decodeTotal;
        private double _inferenceTotal;

        public void Update(CameraPipelineMetrics metrics)
        {
            lock (_sync)
            {
                _samples++;
                _decodeTotal += metrics.DecodeMilliseconds;
                _inferenceTotal += metrics.InferenceMilliseconds;
                _status = _status with
                {
                    Online = true,
                    FramesPerSecond = metrics.FramesPerSecond,
                    FramesProcessed = metrics.FramesProcessed,
                    FramesDropped = metrics.FramesDropped,
                    LatencyMilliseconds = metrics.TotalMilliseconds,
                    AverageDecodeMilliseconds = _decodeTotal / _samples,
                    AverageInferenceMilliseconds = _inferenceTotal / _samples,
                    InferenceFps = metrics.InferenceFps,
                    DroppedInferenceFrames = metrics.DroppedInferenceFrames,
                    QueueDepth = metrics.QueueDepth,
                    Error = null
                };
            }
        }

        public void SetOffline(string? error)
        {
            lock (_sync) _status = _status with { Online = false, FramesPerSecond = 0, InferenceFps = 0, Error = error };
        }

        public CameraRuntimeStatus Snapshot()
        {
            lock (_sync) return _status;
        }
    }
}
