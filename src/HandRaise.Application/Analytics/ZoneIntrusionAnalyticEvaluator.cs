using HandRaise.Application.Events;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Analytics;

public class ZoneIntrusionAnalyticEvaluator : IAnalyticEvaluator
{
    public enum IntrusionState
    {
        Outside,
        Candidate,
        Intrusion,
        ExitGrace
    }

    private readonly string _instanceId;
    private readonly IReadOnlyList<string> _assignedZoneIds;
    private readonly double _confidenceThreshold;
    private readonly double _entryDelayMs;
    private readonly double _exitGraceMs;
    private readonly bool _emitSnapshot;

    private IntrusionState _state = IntrusionState.Outside;
    private double? _candidateSinceMs;
    private double? _intrusionStartedMs;
    private double? _absenceSinceMs;

    private int _lastPersonCount;
    private int? _lastTrackId;
    private string? _lastZoneId;
    private double? _lastConfidence;

    public string AnalyticTypeId => "zone_intrusion";
    public string InstanceId => _instanceId;
    public bool IsEnabled { get; set; } = true;
    public IntrusionState CurrentState => _state;
    public IReadOnlyList<string> AssignedZoneIds => _assignedZoneIds;

    public ZoneIntrusionAnalyticEvaluator(
        string? instanceId = null,
        IReadOnlyList<string>? assignedZoneIds = null,
        double confidenceThreshold = 0.60,
        double entryDelayMs = 500,
        double exitGraceMs = 1000,
        bool emitSnapshot = true)
    {
        _instanceId = string.IsNullOrWhiteSpace(instanceId) ? "zone_intrusion_default" : instanceId.Trim();
        _assignedZoneIds = assignedZoneIds?.Where(z => !string.IsNullOrWhiteSpace(z)).Select(z => z.Trim()).ToArray() ?? [];
        _confidenceThreshold = confidenceThreshold;
        _entryDelayMs = Math.Max(0, entryDelayMs);
        _exitGraceMs = Math.Max(0, exitGraceMs);
        _emitSnapshot = emitSnapshot;
    }

    public AnalyticEvaluationResult Evaluate(AnalyticFrameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsEnabled)
        {
            return AnalyticEvaluationResult.Empty;
        }

        // Semantics: zone_intrusion requires explicit restricted zones.
        // If no assigned zones are configured, do NOT evaluate full frame.
        if (_assignedZoneIds.Count == 0)
        {
            return AnalyticEvaluationResult.Empty;
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

            if (zone == null || !_assignedZoneIds.Contains(zone, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            validPersons.Add(person);
            primaryZoneId ??= zone;
        }

        var validCount = validPersons.Count;
        var genericEvents = new List<AnalyticEvent>();

        // 2. State Machine Transitions
        switch (_state)
        {
            case IntrusionState.Outside:
                if (validCount > 0)
                {
                    _lastPersonCount = validCount;
                    _lastTrackId = validPersons.FirstOrDefault(p => p.TrackId.HasValue)?.TrackId;
                    _lastZoneId = primaryZoneId;
                    _lastConfidence = validPersons.Average(p => (double)p.Detection.Confidence);

                    if (_entryDelayMs <= 0)
                    {
                        _state = IntrusionState.Intrusion;
                        _intrusionStartedMs = monotonicTime;
                        _candidateSinceMs = null;
                        _absenceSinceMs = null;

                        genericEvents.Add(CreateStartedEvent(context.CameraId, utcTimestamp, validPersons, primaryZoneId));
                    }
                    else
                    {
                        _state = IntrusionState.Candidate;
                        _candidateSinceMs = monotonicTime;
                    }
                }
                break;

            case IntrusionState.Candidate:
                if (validCount > 0)
                {
                    _lastPersonCount = validCount;
                    _lastTrackId = validPersons.FirstOrDefault(p => p.TrackId.HasValue)?.TrackId;
                    _lastZoneId = primaryZoneId;
                    _lastConfidence = validPersons.Average(p => (double)p.Detection.Confidence);

                    var candidateStart = _candidateSinceMs ?? monotonicTime;
                    var elapsed = monotonicTime - candidateStart;

                    if (elapsed >= _entryDelayMs)
                    {
                        _state = IntrusionState.Intrusion;
                        _intrusionStartedMs = candidateStart;
                        _candidateSinceMs = null;
                        _absenceSinceMs = null;

                        genericEvents.Add(CreateStartedEvent(context.CameraId, utcTimestamp, validPersons, primaryZoneId));
                    }
                }
                else
                {
                    // Person left before entry delay satisfied
                    _state = IntrusionState.Outside;
                    _candidateSinceMs = null;
                }
                break;

            case IntrusionState.Intrusion:
                if (validCount > 0)
                {
                    // Maintain intrusion without re-emitting events
                    _absenceSinceMs = null;
                    _lastPersonCount = validCount;
                    _lastTrackId = validPersons.FirstOrDefault(p => p.TrackId.HasValue)?.TrackId;
                    _lastZoneId = primaryZoneId;
                    _lastConfidence = validPersons.Average(p => (double)p.Detection.Confidence);
                }
                else
                {
                    // No valid persons remaining in zone
                    if (_exitGraceMs <= 0)
                    {
                        _state = IntrusionState.Outside;
                        var durationMs = Math.Max(0, monotonicTime - (_intrusionStartedMs ?? monotonicTime));
                        genericEvents.Add(CreateEndedEvent(context.CameraId, utcTimestamp, durationMs));
                        ResetTimers();
                    }
                    else
                    {
                        _state = IntrusionState.ExitGrace;
                        _absenceSinceMs = monotonicTime;
                    }
                }
                break;

            case IntrusionState.ExitGrace:
                if (validCount > 0)
                {
                    // Person returned before grace period expired -> restore Intrusion seamlessly
                    _state = IntrusionState.Intrusion;
                    _absenceSinceMs = null;
                    _lastPersonCount = validCount;
                    _lastTrackId = validPersons.FirstOrDefault(p => p.TrackId.HasValue)?.TrackId;
                    _lastZoneId = primaryZoneId;
                    _lastConfidence = validPersons.Average(p => (double)p.Detection.Confidence);
                }
                else
                {
                    var absenceStart = _absenceSinceMs ?? monotonicTime;
                    var elapsed = monotonicTime - absenceStart;

                    if (elapsed >= _exitGraceMs)
                    {
                        _state = IntrusionState.Outside;
                        var durationMs = Math.Max(0, absenceStart - (_intrusionStartedMs ?? absenceStart));
                        genericEvents.Add(CreateEndedEvent(context.CameraId, utcTimestamp, durationMs));
                        ResetTimers();
                    }
                }
                break;
        }

        return new AnalyticEvaluationResult(genericEvents);
    }

    public void Reset()
    {
        _state = IntrusionState.Outside;
        ResetTimers();
        _lastPersonCount = 0;
        _lastTrackId = null;
        _lastZoneId = null;
        _lastConfidence = null;
    }

    private void ResetTimers()
    {
        _candidateSinceMs = null;
        _intrusionStartedMs = null;
        _absenceSinceMs = null;
    }

    private AnalyticEvent CreateStartedEvent(
        string cameraId,
        DateTimeOffset utcTimestamp,
        List<Tracking.TrackedPose> people,
        string? zoneId)
    {
        var primaryTrackId = people.FirstOrDefault(p => p.TrackId.HasValue)?.TrackId;
        var activeTrackIds = people.Where(p => p.TrackId.HasValue).Select(p => p.TrackId!.Value).ToList();
        var zone = zoneId ?? (_assignedZoneIds.Count > 0 ? _assignedZoneIds[0] : "");

        var metadata = new Dictionary<string, object?>
        {
            ["person_count"] = people.Count,
            ["active_track_ids"] = activeTrackIds,
            ["zone_id"] = zone,
            ["zone_name"] = zone,
            ["emit_snapshot"] = _emitSnapshot
        };

        return new AnalyticEvent(
            Id: $"ev-zi-{Guid.NewGuid():N}",
            CameraId: cameraId,
            AnalyticInstanceId: _instanceId,
            AnalyticType: "zone_intrusion",
            EventType: "zone_intrusion_started",
            TimestampUtc: utcTimestamp,
            TrackId: primaryTrackId,
            ZoneId: zone,
            Confidence: _lastConfidence,
            Metadata: metadata,
            SnapshotUrl: null);
    }

    private AnalyticEvent CreateEndedEvent(
        string cameraId,
        DateTimeOffset utcTimestamp,
        double durationMs)
    {
        var zone = _lastZoneId ?? (_assignedZoneIds.Count > 0 ? _assignedZoneIds[0] : "");

        var metadata = new Dictionary<string, object?>
        {
            ["intrusion_duration_ms"] = (long)Math.Round(durationMs),
            ["zone_id"] = zone,
            ["zone_name"] = zone
        };

        return new AnalyticEvent(
            Id: $"ev-zi-{Guid.NewGuid():N}",
            CameraId: cameraId,
            AnalyticInstanceId: _instanceId,
            AnalyticType: "zone_intrusion",
            EventType: "zone_intrusion_ended",
            TimestampUtc: utcTimestamp,
            TrackId: _lastTrackId,
            ZoneId: zone,
            Confidence: _lastConfidence,
            Metadata: metadata,
            SnapshotUrl: null)
        {
            SnapshotJpeg = null
        };
    }
}
