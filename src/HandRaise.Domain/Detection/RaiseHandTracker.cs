namespace HandRaise.Domain.Detection;

public enum TrackPhase
{
    Idle,
    Raising,
    Raised,
    Lowering,
}

public sealed class RaiseHandTracker
{
    private readonly DetectionOptions _options;
    private readonly Dictionary<int, TrackState> _tracks = [];

    public RaiseHandTracker(DetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public IReadOnlyList<RaiseEvent> Update(
        int trackId,
        HandsState handsState,
        DateTimeOffset timestamp)
    {
        if (!_tracks.TryGetValue(trackId, out var state))
        {
            state = new TrackState();
            _tracks.Add(trackId, state);
        }

        if (state.LastUpdateAt is { } previous && timestamp < previous)
        {
            throw new ArgumentException(
                "Los timestamps deben ser monotónicos para cada track.",
                nameof(timestamp));
        }

        state.LastUpdateAt = timestamp;
        switch (state.Phase)
        {
            case TrackPhase.Idle:
                if (handsState.AnyRaised)
                {
                    StartRaising(state, handsState, timestamp);
                }
                return TryRaise(trackId, state, timestamp);

            case TrackPhase.Raising:
                if (!handsState.AnyRaised)
                {
                    ResetToIdle(state);
                    return [];
                }
                state.RaisedFrames++;
                state.CandidateHand = handsState.Hand;
                state.CandidateConfidence = handsState.Confidence;
                return TryRaise(trackId, state, timestamp);

            case TrackPhase.Raised:
                if (!handsState.AnyRaised)
                {
                    state.Phase = TrackPhase.Lowering;
                    state.LoweredFrames = 1;
                }
                return TryLower(trackId, state, timestamp);

            case TrackPhase.Lowering:
                if (handsState.AnyRaised)
                {
                    state.Phase = TrackPhase.Raised;
                    state.LoweredFrames = 0;
                    return [];
                }
                state.LoweredFrames++;
                return TryLower(trackId, state, timestamp);

            default:
                throw new InvalidOperationException($"Estado desconocido: {state.Phase}");
        }
    }

    public void Prune(IEnumerable<int> activeTrackIds)
    {
        ArgumentNullException.ThrowIfNull(activeTrackIds);
        var active = activeTrackIds.ToHashSet();
        foreach (var trackId in _tracks.Keys.Where(id => !active.Contains(id)).ToArray())
        {
            _tracks.Remove(trackId);
        }
    }

    private static void StartRaising(
        TrackState state,
        HandsState handsState,
        DateTimeOffset timestamp)
    {
        state.Phase = TrackPhase.Raising;
        state.RaisedFrames = 1;
        state.RaisingSince = timestamp;
        state.CandidateHand = handsState.Hand;
        state.CandidateConfidence = handsState.Confidence;
    }

    private IReadOnlyList<RaiseEvent> TryRaise(
        int trackId,
        TrackState state,
        DateTimeOffset timestamp)
    {
        if (!RaiseConditionMet(state, timestamp) ||
            !CooldownElapsed(state, timestamp) ||
            state.CandidateHand is null)
        {
            return [];
        }

        var raisedEvent = new RaiseEvent(
            RaiseEventType.HandRaised,
            trackId,
            state.CandidateHand.Value,
            state.CandidateConfidence,
            timestamp);
        state.Phase = TrackPhase.Raised;
        state.RaisedHand = state.CandidateHand;
        state.RaisedConfidence = state.CandidateConfidence;
        state.LastEventAt = timestamp;
        state.RaisedFrames = 0;
        state.RaisingSince = null;
        return [raisedEvent];
    }

    private IReadOnlyList<RaiseEvent> TryLower(
        int trackId,
        TrackState state,
        DateTimeOffset timestamp)
    {
        if (state.Phase != TrackPhase.Lowering ||
            state.LoweredFrames < _options.LowerConsecutiveFrames ||
            !CooldownElapsed(state, timestamp))
        {
            return [];
        }

        if (state.RaisedHand is null)
        {
            ResetToIdle(state);
            return [];
        }

        var loweredEvent = new RaiseEvent(
            RaiseEventType.HandLowered,
            trackId,
            state.RaisedHand.Value,
            state.RaisedConfidence,
            timestamp);
        state.LastEventAt = timestamp;
        ResetToIdle(state);
        return [loweredEvent];
    }

    private bool RaiseConditionMet(TrackState state, DateTimeOffset timestamp)
    {
        if (_options.HoldTime is { } holdTime)
        {
            return state.RaisingSince is { } started && timestamp - started >= holdTime;
        }
        return state.RaisedFrames >= _options.ConsecutiveFrames;
    }

    private bool CooldownElapsed(TrackState state, DateTimeOffset timestamp) =>
        state.LastEventAt is null || timestamp - state.LastEventAt >= _options.Cooldown;

    private static void ResetToIdle(TrackState state)
    {
        state.Phase = TrackPhase.Idle;
        state.RaisedFrames = 0;
        state.LoweredFrames = 0;
        state.RaisingSince = null;
        state.RaisedHand = null;
        state.CandidateHand = null;
        state.CandidateConfidence = 0;
        state.RaisedConfidence = 0;
    }

    private sealed class TrackState
    {
        public TrackPhase Phase { get; set; }
        public int RaisedFrames { get; set; }
        public int LoweredFrames { get; set; }
        public DateTimeOffset? RaisingSince { get; set; }
        public DateTimeOffset? LastEventAt { get; set; }
        public HandSide? RaisedHand { get; set; }
        public HandSide? CandidateHand { get; set; }
        public double CandidateConfidence { get; set; }
        public double RaisedConfidence { get; set; }
        public DateTimeOffset? LastUpdateAt { get; set; }
    }
}

