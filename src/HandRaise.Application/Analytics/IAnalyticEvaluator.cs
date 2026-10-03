using HandRaise.Application.Tracking;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Analytics;

public interface IAnalyticEvaluator
{
    string AnalyticTypeId { get; }
    string InstanceId { get; }
    bool IsEnabled => true;

    AnalyticEvaluationResult Evaluate(AnalyticFrameContext context);

    void Reset();

    // Legacy method for backward compatibility
    TrackingEvaluation Evaluate(
        string cameraId,
        TrackerUpdate update,
        double timestampMilliseconds,
        DateTimeOffset eventTimestampUtc,
        IReadOnlyList<ZoneDefinition> zones)
    {
        var context = new AnalyticFrameContext(
            cameraId,
            timestampMilliseconds,
            eventTimestampUtc,
            0,
            0,
            update,
            zones);
        var result = Evaluate(context);
        return new TrackingEvaluation(result.People, result.LegacyEvents);
    }
}
