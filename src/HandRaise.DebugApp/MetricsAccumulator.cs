using HandRaise.Application.Inference;
using HandRaise.Application.Pipelines;

namespace HandRaise.DebugApp;

internal sealed class MetricsAccumulator
{
    private readonly List<CameraPipelineMetrics> _samples = [];

    public void Add(CameraPipelineMetrics metrics) => _samples.Add(metrics);

    public MetricsSummary Summarize(InferenceExecutionInfo executionInfo)
    {
        if (_samples.Count == 0)
        {
            return new MetricsSummary(
                executionInfo,
                Frames: 0,
                DroppedFrames: 0,
                FramesPerSecond: 0,
                FrameAgeMeanMs: 0,
                FrameAgeP95Ms: 0,
                PreprocessMeanMs: 0,
                PreprocessP95Ms: 0,
                InferenceMeanMs: 0,
                InferenceP95Ms: 0,
                PostprocessMeanMs: 0,
                PostprocessP95Ms: 0,
                OverlayMeanMs: 0,
                OverlayP95Ms: 0,
                TotalMeanMs: 0,
                TotalP95Ms: 0);
        }

        return new MetricsSummary(
            executionInfo,
            _samples.Count,
            _samples[^1].FramesDropped,
            _samples[^1].FramesPerSecond,
            Mean(sample => sample.FrameAgeMilliseconds),
            P95(sample => sample.FrameAgeMilliseconds),
            Mean(sample => sample.PreprocessMilliseconds),
            P95(sample => sample.PreprocessMilliseconds),
            Mean(sample => sample.InferenceMilliseconds),
            P95(sample => sample.InferenceMilliseconds),
            Mean(sample => sample.PostprocessMilliseconds),
            P95(sample => sample.PostprocessMilliseconds),
            Mean(sample => sample.OverlayMilliseconds),
            P95(sample => sample.OverlayMilliseconds),
            Mean(sample => sample.TotalMilliseconds),
            P95(sample => sample.TotalMilliseconds));
    }

    private double Mean(Func<CameraPipelineMetrics, double> selector) => _samples.Average(selector);

    private double P95(Func<CameraPipelineMetrics, double> selector)
    {
        var sorted = _samples.Select(selector).Order().ToArray();
        var index = Math.Clamp((int)Math.Ceiling(sorted.Length * 0.95) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }
}

internal sealed record MetricsSummary(
    InferenceExecutionInfo Execution,
    int Frames,
    long DroppedFrames,
    double FramesPerSecond,
    double FrameAgeMeanMs,
    double FrameAgeP95Ms,
    double PreprocessMeanMs,
    double PreprocessP95Ms,
    double InferenceMeanMs,
    double InferenceP95Ms,
    double PostprocessMeanMs,
    double PostprocessP95Ms,
    double OverlayMeanMs,
    double OverlayP95Ms,
    double TotalMeanMs,
    double TotalP95Ms);
