using HandRaise.Application.Capture;
using HandRaise.Application.Overlay;
using HandRaise.Application.Events;

namespace HandRaise.Application.Pipelines;

public sealed record ProcessedCameraFrame(
    string CameraId,
    VideoFrame Frame,
    IReadOnlyList<EvaluatedPerson> People,
    IReadOnlyList<HandEvent> Events,
    CameraPipelineMetrics Metrics) : IDisposable
{
    public IReadOnlyList<HandRaise.Application.Analytics.ObjectDetection> Objects { get; init; } = [];
    public void Dispose() => Frame.Dispose();
}
