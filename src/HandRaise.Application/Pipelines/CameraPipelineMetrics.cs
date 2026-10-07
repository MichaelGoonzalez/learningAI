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
    double TotalMilliseconds,
    double AnalyticDurationMilliseconds = 0.0,
    int EvaluatorErrors = 0,
    int ActiveAnalyticCount = 0,
    double InferenceFps = 0.0,
    long DroppedInferenceFrames = 0,
    int QueueDepth = 0);
