using HandRaise.Application.Analytics;
using HandRaise.Application.Events;
using HandRaise.Application.Overlay;
using HandRaise.Domain.Detection;

namespace HandRaise.Application.Tracking;

public sealed class TrackedHandEvaluator : HandRaiseAnalyticEvaluator
{
    public TrackedHandEvaluator(DetectionOptions options, string? instanceId = null)
        : base(options, instanceId)
    {
    }
}

public sealed record TrackingEvaluation(
    IReadOnlyList<EvaluatedPerson> People,
    IReadOnlyList<HandEvent> Events);
