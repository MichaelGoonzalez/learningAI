namespace HandRaise.Application.Pipelines;

public sealed record CameraPipelineMetrics(
    long FramesProcessed,
    long FramesDropped,
    double FramesPerSecond,
    double DecodeMilliseconds,
    double FrameAgeMilliseconds,
    double PreprocessMilliseconds,
    double InferenceMilliseconds,
    double PostprocessMilliseconds,
    double OverlayMilliseconds,
    double TotalMilliseconds);
