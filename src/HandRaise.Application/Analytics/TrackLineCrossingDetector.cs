using HandRaise.Application.Tracking;
using HandRaise.Domain.Lines;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Analytics;

public sealed record TrackLineCrossing(
    int? TrackId,
    string LineId,
    string LineName,
    string Direction,
    double Confidence,
    DateTimeOffset TimestampUtc);

public sealed class TrackLineCrossingDetector
{
    private sealed class TrackLineState
    {
        public LineSide LastSide { get; set; } = LineSide.OnLine;
        public double LastCrossTimestampMs { get; set; } = -1;
    }

    private readonly Dictionary<(int TrackId, string LineId), TrackLineState> _states = new();

    public IReadOnlyList<TrackLineCrossing> ProcessFrame(
        AnalyticFrameContext context,
        IReadOnlyList<string>? assignedLineIds,
        double confidenceThreshold = 0.50,
        long cooldownMs = 1000)
    {
        if (context.TrackingUpdate.People.Count == 0 || context.Lines.Count == 0)
        {
            return [];
        }

        var relevantLines = assignedLineIds is { Count: > 0 }
            ? context.Lines.Where(l => l.Enabled && assignedLineIds.Contains(l.Id, StringComparer.OrdinalIgnoreCase)).ToList()
            : context.Lines.Where(l => l.Enabled).ToList();

        if (relevantLines.Count == 0) return [];

        var crossings = new List<TrackLineCrossing>();

        foreach (var pose in context.TrackingUpdate.People)
        {
            if (pose.Detection.Confidence < confidenceThreshold) continue;
            var trackId = pose.TrackId ?? pose.GetHashCode();

            // Compute normalized bottom-center point of person bounding box
            var normX = Math.Clamp(((pose.Detection.Box.Left + pose.Detection.Box.Right) / 2.0) / context.FrameWidth, 0.0, 1.0);
            var normY = Math.Clamp(pose.Detection.Box.Bottom / (double)context.FrameHeight, 0.0, 1.0);
            var currentPos = new Point2D(normX, normY);

            foreach (var line in relevantLines)
            {
                var key = (trackId, line.Id);
                if (!_states.TryGetValue(key, out var state))
                {
                    state = new TrackLineState();
                    _states[key] = state;
                }

                var currentSide = LineGeometry.DetermineSide(currentPos, line.PointA, line.PointB);

                if (state.LastSide != LineSide.OnLine && currentSide != LineSide.OnLine && currentSide != state.LastSide)
                {
                    var isCooldownActive = state.LastCrossTimestampMs >= 0 &&
                                           (context.TimestampMilliseconds - state.LastCrossTimestampMs) < cooldownMs;

                    if (!isCooldownActive)
                    {
                        var direction = state.LastSide == LineSide.SideA && currentSide == LineSide.SideB
                            ? "a_to_b"
                            : "b_to_a";

                        var matchesDirection = line.DirectionMode switch
                        {
                            LineDirectionMode.Bidirectional => true,
                            LineDirectionMode.AToB => direction == "a_to_b",
                            LineDirectionMode.BToA => direction == "b_to_a",
                            _ => true
                        };

                        if (matchesDirection)
                        {
                            state.LastCrossTimestampMs = context.TimestampMilliseconds;
                            crossings.Add(new TrackLineCrossing(
                                TrackId: pose.TrackId,
                                LineId: line.Id,
                                LineName: line.Name,
                                Direction: direction,
                                Confidence: pose.Detection.Confidence,
                                TimestampUtc: context.TimestampUtc));
                        }
                    }
                }

                if (currentSide != LineSide.OnLine)
                {
                    state.LastSide = currentSide;
                }
            }
        }

        // Cleanup dead tracks
        if (context.TrackingUpdate.ActiveTrackIds.Count > 0)
        {
            var keysToRemove = _states.Keys
                .Where(k => !context.TrackingUpdate.ActiveTrackIds.Contains(k.TrackId))
                .ToList();

            foreach (var k in keysToRemove)
            {
                _states.Remove(k);
            }
        }

        return crossings;
    }

    public void Reset()
    {
        _states.Clear();
    }
}

