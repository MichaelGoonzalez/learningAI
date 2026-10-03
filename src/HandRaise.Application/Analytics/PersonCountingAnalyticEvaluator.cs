using HandRaise.Domain.Analytics;

namespace HandRaise.Application.Analytics;

public sealed class PersonCountingAnalyticEvaluator : IAnalyticEvaluator
{
    private readonly string _instanceId;
    private readonly IReadOnlyList<string> _assignedLineIds;
    private readonly double _confidenceThreshold;
    private readonly string _entryDirection;
    private readonly int _initialOccupancy;
    private readonly int _minimumOccupancy;
    private readonly int _maximumOccupancy;
    private readonly TrackLineCrossingDetector _detector = new();

    private int _entries;
    private int _exits;
    private int _currentOccupancy;
    private bool _thresholdReached;

    public string AnalyticTypeId => "person_counting";
    public string InstanceId => _instanceId;

    public int CurrentOccupancy => _currentOccupancy;
    public int Entries => _entries;
    public int Exits => _exits;

    public PersonCountingAnalyticEvaluator(
        string? instanceId = null,
        IReadOnlyList<string>? assignedLineIds = null,
        double confidenceThreshold = 0.60,
        string entryDirection = "a_to_b",
        int initialOccupancy = 0,
        int minimumOccupancy = 0,
        int maximumOccupancy = 0)
    {
        _instanceId = string.IsNullOrWhiteSpace(instanceId) ? $"inst-pc-{Guid.NewGuid().ToString("N")[..6]}" : instanceId;
        _assignedLineIds = assignedLineIds ?? [];
        _confidenceThreshold = confidenceThreshold;
        _entryDirection = string.IsNullOrWhiteSpace(entryDirection) ? "a_to_b" : entryDirection;
        _initialOccupancy = initialOccupancy;
        _minimumOccupancy = minimumOccupancy;
        _maximumOccupancy = maximumOccupancy;

        _currentOccupancy = Math.Max(_minimumOccupancy, _initialOccupancy);
        _thresholdReached = _maximumOccupancy > 0 && _currentOccupancy >= _maximumOccupancy;
    }

    public AnalyticEvaluationResult Evaluate(AnalyticFrameContext context)
    {
        if (context.TrackingUpdate.People.Count == 0 || context.Lines.Count == 0)
        {
            return AnalyticEvaluationResult.Empty;
        }

        var crossings = _detector.ProcessFrame(context, _assignedLineIds, _confidenceThreshold, cooldownMs: 1000);
        if (crossings.Count == 0)
        {
            return AnalyticEvaluationResult.Empty;
        }

        var events = new List<AnalyticEvent>();

        foreach (var crossing in crossings)
        {
            var isEntry = string.Equals(crossing.Direction, _entryDirection, StringComparison.OrdinalIgnoreCase);
            if (isEntry)
            {
                _entries++;
            }
            else
            {
                _exits++;
            }

            _currentOccupancy = Math.Max(_minimumOccupancy, _initialOccupancy + _entries - _exits);

            var countMetadata = new Dictionary<string, object?>
            {
                ["entries"] = _entries,
                ["exits"] = _exits,
                ["current_occupancy"] = _currentOccupancy,
                ["direction"] = crossing.Direction,
                ["line_id"] = crossing.LineId,
                ["line_name"] = crossing.LineName
            };

            var countEvent = new AnalyticEvent(
                Id: $"ev-pc-{Guid.NewGuid():N}",
                CameraId: context.CameraId,
                AnalyticInstanceId: _instanceId,
                AnalyticType: "person_counting",
                EventType: "person_count_updated",
                TimestampUtc: context.TimestampUtc,
                TrackId: crossing.TrackId,
                ZoneId: null,
                Confidence: crossing.Confidence,
                Metadata: countMetadata,
                SnapshotUrl: null);

            events.Add(countEvent);

            // Threshold reach / rearm evaluation
            if (_maximumOccupancy > 0)
            {
                if (_currentOccupancy >= _maximumOccupancy && !_thresholdReached)
                {
                    _thresholdReached = true;

                    var threshMetadata = new Dictionary<string, object?>
                    {
                        ["current_occupancy"] = _currentOccupancy,
                        ["maximum_occupancy"] = _maximumOccupancy,
                        ["entries"] = _entries,
                        ["exits"] = _exits,
                        ["line_id"] = crossing.LineId,
                        ["line_name"] = crossing.LineName
                    };

                    var threshEvent = new AnalyticEvent(
                        Id: $"ev-pc-thresh-{Guid.NewGuid():N}",
                        CameraId: context.CameraId,
                        AnalyticInstanceId: _instanceId,
                        AnalyticType: "person_counting",
                        EventType: "occupancy_threshold_reached",
                        TimestampUtc: context.TimestampUtc,
                        TrackId: crossing.TrackId,
                        ZoneId: null,
                        Confidence: crossing.Confidence,
                        Metadata: threshMetadata,
                        SnapshotUrl: null);

                    events.Add(threshEvent);
                }
                else if (_currentOccupancy < _maximumOccupancy && _thresholdReached)
                {
                    // Rearm threshold alert
                    _thresholdReached = false;
                }
            }
        }

        return new AnalyticEvaluationResult(events);
    }

    public void Reset()
    {
        _detector.Reset();
        _entries = 0;
        _exits = 0;
        _currentOccupancy = Math.Max(_minimumOccupancy, _initialOccupancy);
        _thresholdReached = _maximumOccupancy > 0 && _currentOccupancy >= _maximumOccupancy;
    }
}
