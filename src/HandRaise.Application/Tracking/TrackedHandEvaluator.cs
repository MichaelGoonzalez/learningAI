using HandRaise.Application.Events;
using HandRaise.Application.Overlay;
using HandRaise.Domain.Detection;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Tracking;

public sealed class TrackedHandEvaluator
{
    private readonly DetectionOptions _options;
    private readonly RaiseHandTracker _stateTracker;
    private readonly Dictionary<int, TrackPhase> _visualPhases = [];

    public TrackedHandEvaluator(DetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _stateTracker = new RaiseHandTracker(options);
    }

    public TrackingEvaluation Evaluate(
        string cameraId,
        TrackerUpdate update,
        double timestampMilliseconds,
        DateTimeOffset eventTimestampUtc,
        IReadOnlyList<ZoneDefinition> zones)
    {
        var monotonicTimestamp = DateTimeOffset.UnixEpoch.AddMilliseconds(timestampMilliseconds);
        var people = new List<EvaluatedPerson>(update.People.Count);
        var events = new List<HandEvent>();
        foreach (var person in update.People)
        {
            var hands = HandEvaluator.Evaluate(person.Detection.Keypoints, _options);
            var center = new Point2D(
                (person.Detection.Box.Left + person.Detection.Box.Right) / 2,
                (person.Detection.Box.Top + person.Detection.Box.Bottom) / 2);
            var zone = ZoneGeometry.FindZone(center, zones);
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

                    events.Add(MapEvent(cameraId, raisedEvent, eventTimestampUtc, zone));
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

        _stateTracker.Prune(update.ActiveTrackIds);
        foreach (var trackId in _visualPhases.Keys.Where(id => !update.ActiveTrackIds.Contains(id)).ToArray())
        {
            _visualPhases.Remove(trackId);
        }

        return new TrackingEvaluation(people, events);
    }

    public void Reset()
    {
        _stateTracker.Prune([]);
        _visualPhases.Clear();
    }

    private static HandEvent MapEvent(
        string cameraId,
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
        return new HandEvent(
            id,
            type,
            cameraId,
            source.TrackId,
            hand,
            zone,
            source.Confidence,
            timestamp);
    }
}

public sealed record TrackingEvaluation(
    IReadOnlyList<EvaluatedPerson> People,
    IReadOnlyList<HandEvent> Events);
