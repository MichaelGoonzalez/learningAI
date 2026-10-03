using HandRaise.Application.Events;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Analytics;

public class PersonPresenceAnalyticEvaluator : IAnalyticEvaluator
{
    public enum PresenceState
    {
        Absent,
        Candidate,
        Present
    }

    private readonly string _instanceId;
    private readonly IReadOnlyList<string> _assignedZoneIds;
    private readonly double _confidenceThreshold;
    private readonly double _minPresenceMs;
    private readonly double _absenceGraceMs;
    private readonly bool _emitSnapshot;

    private PresenceState _state = PresenceState.Absent;
    private double? _candidateSinceMs;
    private double? _presenceStartedMs;
    private double? _absenceSinceMs;

    private int _lastPersonCount;
    private int? _lastTrackId;
    private string? _lastZoneId;
    private double? _lastConfidence;

    public string AnalyticTypeId => "person_presence";
    public string InstanceId => _instanceId;
    public bool IsEnabled { get; set; } = true;
    public PresenceState CurrentState => _state;

    public PersonPresenceAnalyticEvaluator(
        string? instanceId = null,
        IReadOnlyList<string>? assignedZoneIds = null,
        double confidenceThreshold = 0.60,
        double minPresenceMs = 1000,
        double absenceGraceMs = 1500,
        bool emitSnapshot = true)
    {
        _instanceId = string.IsNullOrWhiteSpace(instanceId) ? "person_presence_default" : instanceId.Trim();
        _assignedZoneIds = assignedZoneIds?.Where(z => !string.IsNullOrWhiteSpace(z)).Select(z => z.Trim()).ToArray() ?? [];
        _confidenceThreshold = confidenceThreshold;
        _minPresenceMs = Math.Max(0, minPresenceMs);
        _absenceGraceMs = Math.Max(0, absenceGraceMs);
        _emitSnapshot = emitSnapshot;
    }

    public AnalyticEvaluationResult Evaluate(AnalyticFrameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsEnabled)
        {
            return new AnalyticEvaluationResult([], [], []);
        }

        var monotonicTime = context.TimestampMilliseconds;
        var utcTimestamp = context.TimestampUtc;

        // 1. Filter persons in frame matching confidence & assigned zones
        var validPersons = new List<Tracking.TrackedPose>();
        string? primaryZoneId = null;

        foreach (var person in context.TrackingUpdate.People)
        {
            var personConfidence = (double)person.Detection.Confidence;
            if (personConfidence < _confidenceThreshold)
            {
                continue;
            }

            var center = new Point2D(
                (person.Detection.Box.Left + person.Detection.Box.Right) / 2.0,
                (person.Detection.Box.Top + person.Detection.Box.Bottom) / 2.0);

            var zone = ZoneGeometry.FindZone(center, context.Zones);

            if (_assignedZoneIds.Count > 0)
            {
                if (zone == null || !_assignedZoneIds.Contains(zone, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            validPersons.Add(person);
            primaryZoneId ??= zone ?? (_assignedZoneIds.Count > 0 ? _assignedZoneIds[0] : null);
        }

        var validCount = validPersons.Count;
        var genericEvents = new List<AnalyticEvent>();

        // 2. State Machine Transitions
        switch (_state)
        {
            case PresenceState.Absent:
                if (validCount >= 1)
                {
                    UpdatePersonMetrics(validPersons, primaryZoneId);

                    if (_minPresenceMs <= 0)
                    {
                        _state = PresenceState.Present;
                        _presenceStartedMs = monotonicTime;
                        _absenceSinceMs = null;
                        genericEvents.Add(CreateStartedEvent(context.CameraId, utcTimestamp, validCount));
                    }
                    else
                    {
                        _state = PresenceState.Candidate;
                        _candidateSinceMs = monotonicTime;
                    }
                }
                break;

            case PresenceState.Candidate:
                if (validCount >= 1)
                {
                    UpdatePersonMetrics(validPersons, primaryZoneId);

                    if (_candidateSinceMs.HasValue && (monotonicTime - _candidateSinceMs.Value) >= _minPresenceMs)
                    {
                        _state = PresenceState.Present;
                        _presenceStartedMs = _candidateSinceMs.Value;
                        _absenceSinceMs = null;
                        genericEvents.Add(CreateStartedEvent(context.CameraId, utcTimestamp, validCount));
                    }
                }
                else
                {
                    _state = PresenceState.Absent;
                    _candidateSinceMs = null;
                }
                break;

            case PresenceState.Present:
                if (validCount >= 1)
                {
                    _absenceSinceMs = null;
                    UpdatePersonMetrics(validPersons, primaryZoneId);
                }
                else
                {
                    if (!_absenceSinceMs.HasValue)
                    {
                        _absenceSinceMs = monotonicTime;
                    }

                    if ((monotonicTime - _absenceSinceMs.Value) >= _absenceGraceMs)
                    {
                        _state = PresenceState.Absent;
                        var duration = Math.Max(0, (long)(monotonicTime - (_presenceStartedMs ?? monotonicTime)));
                        genericEvents.Add(CreateEndedEvent(context.CameraId, utcTimestamp, duration));

                        _candidateSinceMs = null;
                        _presenceStartedMs = null;
                        _absenceSinceMs = null;
                    }
                }
                break;
        }

        return new AnalyticEvaluationResult(genericEvents, [], []);
    }

    public void Reset()
    {
        _state = PresenceState.Absent;
        _candidateSinceMs = null;
        _presenceStartedMs = null;
        _absenceSinceMs = null;
        _lastPersonCount = 0;
        _lastTrackId = null;
        _lastZoneId = null;
        _lastConfidence = null;
    }

    private void UpdatePersonMetrics(List<Tracking.TrackedPose> validPersons, string? primaryZoneId)
    {
        _lastPersonCount = validPersons.Count;
        if (validPersons.Count > 0)
        {
            _lastTrackId = validPersons[0].TrackId ?? _lastTrackId;
            _lastConfidence = (double)validPersons[0].Detection.Confidence;
        }
        if (primaryZoneId != null)
        {
            _lastZoneId = primaryZoneId;
        }
        else if (_assignedZoneIds.Count > 0)
        {
            _lastZoneId = _assignedZoneIds[0];
        }
    }

    private AnalyticEvent CreateStartedEvent(string cameraId, DateTimeOffset timestampUtc, int personCount)
    {
        var id = $"{cameraId}:{_instanceId}:{timestampUtc.ToUnixTimeMilliseconds()}:person_presence_started";
        var metadata = new Dictionary<string, object?>
        {
            ["person_count"] = personCount,
            ["emit_snapshot"] = _emitSnapshot
        };

        return new AnalyticEvent(
            Id: id,
            CameraId: cameraId,
            AnalyticInstanceId: _instanceId,
            AnalyticType: "person_presence",
            EventType: "person_presence_started",
            TimestampUtc: timestampUtc,
            TrackId: _lastTrackId,
            ZoneId: _lastZoneId,
            Confidence: _lastConfidence,
            Metadata: metadata);
    }

    private AnalyticEvent CreateEndedEvent(string cameraId, DateTimeOffset timestampUtc, long durationMs)
    {
        var id = $"{cameraId}:{_instanceId}:{timestampUtc.ToUnixTimeMilliseconds()}:person_presence_ended";
        var metadata = new Dictionary<string, object?>
        {
            ["person_count"] = 0,
            ["last_person_count"] = _lastPersonCount,
            ["presence_duration_ms"] = durationMs,
            ["emit_snapshot"] = false
        };

        return new AnalyticEvent(
            Id: id,
            CameraId: cameraId,
            AnalyticInstanceId: _instanceId,
            AnalyticType: "person_presence",
            EventType: "person_presence_ended",
            TimestampUtc: timestampUtc,
            TrackId: _lastTrackId,
            ZoneId: _lastZoneId,
            Confidence: _lastConfidence,
            Metadata: metadata);
    }
}
