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
    string? Error);

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
                    Error = null
                };
            }
        }

        public void SetOffline(string? error)
        {
            lock (_sync) _status = _status with { Online = false, FramesPerSecond = 0, Error = error };
        }

        public CameraRuntimeStatus Snapshot()
        {
            lock (_sync) return _status;
        }
    }
}
