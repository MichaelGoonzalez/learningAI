using HandRaise.Application.Events;
using HandRaise.Application.Overlay;
using HandRaise.Application.Tracking;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Lines;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Analytics;

public sealed class AnalyticFrameContext
{
    public string CameraId { get; }
    public double TimestampMilliseconds { get; }
    public DateTimeOffset TimestampUtc { get; }
    public int FrameWidth { get; }
    public int FrameHeight { get; }
    public TrackerUpdate TrackingUpdate { get; }
    public IReadOnlyList<ZoneDefinition> Zones { get; }
    public IReadOnlyList<LineDefinition> Lines { get; }

    public AnalyticFrameContext(
        string cameraId,
        double timestampMilliseconds,
        DateTimeOffset timestampUtc,
        int frameWidth,
        int frameHeight,
        TrackerUpdate trackingUpdate,
        IReadOnlyList<ZoneDefinition> zones,
        IReadOnlyList<LineDefinition>? lines = null)
    {
        CameraId = cameraId;
        TimestampMilliseconds = timestampMilliseconds;
        TimestampUtc = timestampUtc;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        TrackingUpdate = trackingUpdate;
        Zones = zones;
        Lines = lines ?? [];
    }
}

public sealed class AnalyticEvaluationResult
{
    public IReadOnlyList<AnalyticEvent> Events { get; }
    public IReadOnlyList<EvaluatedPerson> People { get; }
    public IReadOnlyList<HandEvent> LegacyEvents { get; }

    public AnalyticEvaluationResult(
        IReadOnlyList<AnalyticEvent> events,
        IReadOnlyList<EvaluatedPerson>? people = null,
        IReadOnlyList<HandEvent>? legacyEvents = null)
    {
        Events = events;
        People = people ?? [];
        LegacyEvents = legacyEvents ?? [];
    }

    public static AnalyticEvaluationResult Empty { get; } = new([], [], []);
}
