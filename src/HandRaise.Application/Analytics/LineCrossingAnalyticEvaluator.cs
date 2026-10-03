using HandRaise.Domain.Analytics;

namespace HandRaise.Application.Analytics;

public sealed class LineCrossingAnalyticEvaluator : IAnalyticEvaluator
{
    private readonly string _instanceId;
    private readonly IReadOnlyList<string> _assignedLineIds;
    private readonly double _confidenceThreshold;
    private readonly long _cooldownMs;
    private readonly bool _emitSnapshot;
    private readonly TrackLineCrossingDetector _detector = new();

    public string AnalyticTypeId => "line_crossing";
    public string InstanceId => _instanceId;

    public LineCrossingAnalyticEvaluator(
        string? instanceId = null,
        IReadOnlyList<string>? assignedLineIds = null,
        double confidenceThreshold = 0.60,
        long cooldownMs = 1500,
        bool emitSnapshot = true)
    {
        _instanceId = string.IsNullOrWhiteSpace(instanceId) ? $"inst-lc-{Guid.NewGuid().ToString("N")[..6]}" : instanceId;
        _assignedLineIds = assignedLineIds ?? [];
        _confidenceThreshold = confidenceThreshold;
        _cooldownMs = cooldownMs;
        _emitSnapshot = emitSnapshot;
    }

    public AnalyticEvaluationResult Evaluate(AnalyticFrameContext context)
    {
        if (context.TrackingUpdate.People.Count == 0 || context.Lines.Count == 0)
        {
            return AnalyticEvaluationResult.Empty;
        }

        var crossings = _detector.ProcessFrame(context, _assignedLineIds, _confidenceThreshold, _cooldownMs);
        if (crossings.Count == 0)
        {
            return AnalyticEvaluationResult.Empty;
        }

        var events = new List<AnalyticEvent>();

        foreach (var crossing in crossings)
        {
            var metadata = new Dictionary<string, object?>
            {
                ["direction"] = crossing.Direction,
                ["line_id"] = crossing.LineId,
                ["line_name"] = crossing.LineName
            };

            var ev = new AnalyticEvent(
                Id: $"ev-lc-{Guid.NewGuid():N}",
                CameraId: context.CameraId,
                AnalyticInstanceId: _instanceId,
                AnalyticType: "line_crossing",
                EventType: "line_crossed",
                TimestampUtc: context.TimestampUtc,
                TrackId: crossing.TrackId,
                ZoneId: null,
                Confidence: crossing.Confidence,
                Metadata: metadata,
                SnapshotUrl: null);

            events.Add(ev);
        }

        return new AnalyticEvaluationResult(events);
    }

    public void Reset()
    {
        _detector.Reset();
    }
}
