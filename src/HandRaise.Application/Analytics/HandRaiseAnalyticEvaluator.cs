using HandRaise.Application.Events;
using HandRaise.Application.Overlay;
using HandRaise.Application.Tracking;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Detection;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Analytics;

public class HandRaiseAnalyticEvaluator : IAnalyticEvaluator
{
    private readonly DetectionOptions _options;
    private readonly RaiseHandTracker _stateTracker;
    private readonly Dictionary<int, TrackPhase> _visualPhases = [];
    private readonly string _instanceId;

    public string AnalyticTypeId => "hand_raise";
    public string InstanceId => _instanceId;
    public bool IsEnabled { get; set; } = true;

    public HandRaiseAnalyticEvaluator(DetectionOptions options, string? instanceId = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _instanceId = instanceId ?? "hand_raise_default";
        _stateTracker = new RaiseHandTracker(options);
    }

    public AnalyticEvaluationResult Evaluate(AnalyticFrameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var monotonicTimestamp = DateTimeOffset.UnixEpoch.AddMilliseconds(context.TimestampMilliseconds);
        var people = new List<EvaluatedPerson>(context.TrackingUpdate.People.Count);
        var legacyEvents = new List<HandEvent>();
        var genericEvents = new List<AnalyticEvent>();

        foreach (var person in context.TrackingUpdate.People)
        {
            var hands = HandEvaluator.Evaluate(person.Detection.Keypoints, _options);
            var center = new Point2D(
                (person.Detection.Box.Left + person.Detection.Box.Right) / 2,
                (person.Detection.Box.Top + person.Detection.Box.Bottom) / 2);
            var zone = ZoneGeometry.FindZone(center, context.Zones);
            TrackPhase? phase = null;

            if (person.TrackId is { } trackId)
            {
                var raisedEvents = _stateTracker.Update(trackId, hands, monotonicTimestamp);
                foreach (var raisedEvent in raisedEvents)
                {
                    phase = raisedEvent.Type == RaiseEventType.HandRaised
                        ? TrackPhase.Raised
                        : TrackPhase.Idle;

                    if (phase == TrackPhase.Idle)
                    {
                        _visualPhases.Remove(trackId);
                    }
                    else
                    {
                        _visualPhases[trackId] = phase.Value;
                    }

                    var legacyEvent = MapLegacyEvent(context.CameraId, _instanceId, raisedEvent, context.TimestampUtc, zone);
                    legacyEvents.Add(legacyEvent);

                    genericEvents.Add(MapGenericEvent(context.CameraId, _instanceId, raisedEvent, context.TimestampUtc, zone));
                }

                if (raisedEvents.Count == 0)
                {
                    if (_visualPhases.TryGetValue(trackId, out var current))
                    {
                        if (current == TrackPhase.Raising && !hands.AnyRaised)
                        {
                            _visualPhases.Remove(trackId);
                        }
                        else
                        {
                            phase = current;
                        }
                    }
                    else if (hands.AnyRaised)
                    {
                        phase = TrackPhase.Raising;
                        _visualPhases[trackId] = phase.Value;
                    }
                }
            }

            people.Add(new EvaluatedPerson(person.Detection, hands, zone, person.TrackId, phase));
        }

        _stateTracker.Prune(context.TrackingUpdate.ActiveTrackIds);
        foreach (var trackId in _visualPhases.Keys.Where(id => !context.TrackingUpdate.ActiveTrackIds.Contains(id)).ToArray())
        {
            _visualPhases.Remove(trackId);
        }

        return new AnalyticEvaluationResult(genericEvents, people, legacyEvents);
    }

    public TrackingEvaluation Evaluate(
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

    public void Reset()
    {
        _stateTracker.Prune([]);
        _visualPhases.Clear();
    }

    private static HandEvent MapLegacyEvent(
        string cameraId,
        string instanceId,
        RaiseEvent source,
        DateTimeOffset eventTimestampUtc,
        string? zone)
    {
        var type = source.Type == RaiseEventType.HandRaised ? "hand_raised" : "hand_lowered";
        var hand = source.Hand switch
        {
            HandSide.Left => "left",
            HandSide.Right => "right",
            HandSide.Both => "both",
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        var timestamp = eventTimestampUtc.ToUniversalTime();
        var id = $"{cameraId}:{source.TrackId}:{timestamp.ToUnixTimeMilliseconds()}:{type}";
        var metadata = new Dictionary<string, object?>
        {
            ["hand"] = hand,
            ["hand_side"] = source.Hand.ToString().ToLowerInvariant(),
            ["analytic_instance_id"] = instanceId,
            ["analytic_type"] = "hand_raise"
        };
        return new HandEvent(
            id,
            type,
            cameraId,
            source.TrackId,
            hand,
            zone,
            source.Confidence,
            timestamp,
            Metadata: metadata);
    }

    private static AnalyticEvent MapGenericEvent(
        string cameraId,
        string instanceId,
        RaiseEvent source,
        DateTimeOffset eventTimestampUtc,
        string? zone)
    {
        var type = source.Type == RaiseEventType.HandRaised ? "hand_raised" : "hand_lowered";
        var hand = source.Hand switch
        {
            HandSide.Left => "left",
            HandSide.Right => "right",
            HandSide.Both => "both",
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        var timestamp = eventTimestampUtc.ToUniversalTime();
        var id = $"{cameraId}:{source.TrackId}:{timestamp.ToUnixTimeMilliseconds()}:{type}";
        var metadata = new Dictionary<string, object?>
        {
            ["hand"] = hand,
            ["hand_side"] = source.Hand.ToString().ToLowerInvariant()
        };

        return new AnalyticEvent(
            Id: id,
            CameraId: cameraId,
            AnalyticInstanceId: instanceId,
            AnalyticType: "hand_raise",
            EventType: type,
            TimestampUtc: timestamp,
            TrackId: source.TrackId,
            ZoneId: zone,
            Confidence: source.Confidence,
            Metadata: metadata);
    }
}
