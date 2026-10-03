using HandRaise.Application.Analytics;
using HandRaise.Application.Inference;
using HandRaise.Application.Tracking;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Tests;

public class ZoneIntrusionAnalyticEvaluatorTests
{
    private static TrackedPose CreatePose(
        int trackId,
        float left = 100f,
        float top = 100f,
        float right = 200f,
        float bottom = 200f,
        float confidence = 0.90f)
    {
        return new TrackedPose(
            new PosePerson(new BoundingBox(left, top, right, bottom), confidence, 0, []),
            trackId);
    }

    private static AnalyticFrameContext CreateContext(
        double timestampMs,
        IReadOnlyList<TrackedPose> people,
        IReadOnlyList<ZoneDefinition>? zones = null,
        string cameraId = "cam-1")
    {
        var activeTrackIds = people.Where(p => p.TrackId.HasValue).Select(p => p.TrackId!.Value).ToHashSet();
        var update = new TrackerUpdate(people, activeTrackIds);
        return new AnalyticFrameContext(
            cameraId,
            timestampMs,
            DateTimeOffset.UtcNow,
            1920,
            1080,
            update,
            zones ?? []);
    }

    private static readonly IReadOnlyList<ZoneDefinition> TestZones =
    [
        new ZoneDefinition("restricted-zone", [new(0, 0), new(500, 0), new(500, 500), new(0, 500)])
    ];

    [Fact]
    public void InitialStateIsOutside()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: ["restricted-zone"],
            entryDelayMs: 500,
            exitGraceMs: 1000);

        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Outside, evaluator.CurrentState);
    }

    [Fact]
    public void NoAssignedZonesEvaluatesToNoIntrusion()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: [],
            entryDelayMs: 0);

        var person = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200);
        var context = CreateContext(100, [person], TestZones);

        var result = evaluator.Evaluate(context);

        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Outside, evaluator.CurrentState);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void OutsideToCandidateWhenPersonEntersZone()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: ["restricted-zone"],
            entryDelayMs: 500,
            exitGraceMs: 1000);

        var person = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200);
        var context = CreateContext(100, [person], TestZones);

        var result = evaluator.Evaluate(context);

        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Candidate, evaluator.CurrentState);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void CandidateReturnsToOutsideIfPersonLeavesBeforeEntryDelay()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: ["restricted-zone"],
            entryDelayMs: 500,
            exitGraceMs: 1000);

        var person = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200);

        // Frame 1 at t=100 -> enters candidate
        evaluator.Evaluate(CreateContext(100, [person], TestZones));
        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Candidate, evaluator.CurrentState);

        // Frame 2 at t=300 (before 500ms elapsed) -> no person
        var result = evaluator.Evaluate(CreateContext(300, [], TestZones));

        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Outside, evaluator.CurrentState);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void CandidateTransitsToIntrusionAndEmitsStartedOnceAfterEntryDelay()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: ["restricted-zone"],
            entryDelayMs: 500,
            exitGraceMs: 1000,
            emitSnapshot: true);

        var person = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200);

        // Frame 1 at t=100
        var res1 = evaluator.Evaluate(CreateContext(100, [person], TestZones));
        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Candidate, evaluator.CurrentState);
        Assert.Empty(res1.Events);

        // Frame 2 at t=600 (elapsed = 500ms == entryDelay)
        var res2 = evaluator.Evaluate(CreateContext(600, [person], TestZones));
        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Intrusion, evaluator.CurrentState);
        var startedEvent = Assert.Single(res2.Events);
        Assert.Equal("zone_intrusion_started", startedEvent.EventType);
        Assert.Equal("zone_intrusion", startedEvent.AnalyticType);
        Assert.True(Convert.ToBoolean(startedEvent.Metadata?["emit_snapshot"])); // Snapshot requested

        // Frame 3 at t=800 -> stays Intrusion, no duplicate started event
        var res3 = evaluator.Evaluate(CreateContext(800, [person], TestZones));
        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Intrusion, evaluator.CurrentState);
        Assert.Empty(res3.Events);
    }

    [Fact]
    public void ZeroEntryDelayTransitsDirectlyToIntrusion()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: ["restricted-zone"],
            entryDelayMs: 0,
            exitGraceMs: 1000);

        var person = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200);
        var result = evaluator.Evaluate(CreateContext(100, [person], TestZones));

        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Intrusion, evaluator.CurrentState);
        var startedEvent = Assert.Single(result.Events);
        Assert.Equal("zone_intrusion_started", startedEvent.EventType);
    }

    [Fact]
    public void IntrusionTransitsToExitGraceWhenPersonDisappears()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: ["restricted-zone"],
            entryDelayMs: 0,
            exitGraceMs: 1000);

        var person = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200);

        evaluator.Evaluate(CreateContext(100, [person], TestZones));
        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Intrusion, evaluator.CurrentState);

        // Person disappears at t=200
        var res = evaluator.Evaluate(CreateContext(200, [], TestZones));
        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.ExitGrace, evaluator.CurrentState);
        Assert.Empty(res.Events);
    }

    [Fact]
    public void ReentryDuringExitGraceRestoresIntrusionWithoutNewStartedEvent()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: ["restricted-zone"],
            entryDelayMs: 0,
            exitGraceMs: 1000);

        var person = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200);

        evaluator.Evaluate(CreateContext(100, [person], TestZones));
        evaluator.Evaluate(CreateContext(200, [], TestZones));
        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.ExitGrace, evaluator.CurrentState);

        // Person re-enters at t=500 (within 1000ms grace)
        var res = evaluator.Evaluate(CreateContext(500, [person], TestZones));
        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Intrusion, evaluator.CurrentState);
        Assert.Empty(res.Events); // No redundant started event
    }

    [Fact]
    public void ExitGraceExpirationTransitsToOutsideAndEmitsEndedEvent()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: ["restricted-zone"],
            entryDelayMs: 0,
            exitGraceMs: 1000);

        var person = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200);

        // t=100 -> start
        evaluator.Evaluate(CreateContext(100, [person], TestZones));
        // t=500 -> disappear, start grace
        evaluator.Evaluate(CreateContext(500, [], TestZones));
        // t=1600 -> elapsed = 1100ms >= 1000ms grace
        var res = evaluator.Evaluate(CreateContext(1600, [], TestZones));

        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Outside, evaluator.CurrentState);
        var endedEvent = Assert.Single(res.Events);
        Assert.Equal("zone_intrusion_ended", endedEvent.EventType);
        Assert.Equal("restricted-zone", endedEvent.ZoneId);
        Assert.Null(endedEvent.SnapshotJpeg); // No snapshot on end

        // Check intrusion_duration_ms
        Assert.NotNull(endedEvent.Metadata);
        Assert.True(endedEvent.Metadata.ContainsKey("intrusion_duration_ms"));
        var duration = Convert.ToInt64(endedEvent.Metadata["intrusion_duration_ms"]);
        Assert.Equal(400, duration); // 500 - 100
    }

    [Fact]
    public void ZeroExitGraceEmitsEndedImmediatelyOnDisappearance()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: ["restricted-zone"],
            entryDelayMs: 0,
            exitGraceMs: 0);

        var person = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200);

        evaluator.Evaluate(CreateContext(100, [person], TestZones));
        var res = evaluator.Evaluate(CreateContext(400, [], TestZones));

        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Outside, evaluator.CurrentState);
        var endedEvent = Assert.Single(res.Events);
        Assert.Equal("zone_intrusion_ended", endedEvent.EventType);
        Assert.NotNull(endedEvent.Metadata);
        Assert.Equal(300L, Convert.ToInt64(endedEvent.Metadata["intrusion_duration_ms"]));
    }

    [Fact]
    public void MultiplePeopleMaintainIntrusionSeamlessly()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: ["restricted-zone"],
            entryDelayMs: 0,
            exitGraceMs: 500);

        var person1 = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200);
        var person2 = CreatePose(2, left: 250, top: 250, right: 350, bottom: 350);

        // Person 1 enters
        evaluator.Evaluate(CreateContext(100, [person1], TestZones));
        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Intrusion, evaluator.CurrentState);

        // Person 2 also enters
        evaluator.Evaluate(CreateContext(200, [person1, person2], TestZones));
        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Intrusion, evaluator.CurrentState);

        // Person 1 leaves, Person 2 remains
        var res = evaluator.Evaluate(CreateContext(300, [person2], TestZones));
        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Intrusion, evaluator.CurrentState);
        Assert.Empty(res.Events); // Still in intrusion
    }

    [Fact]
    public void TrackHandoffDoesNotCauseInterruption()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: ["restricted-zone"],
            entryDelayMs: 0,
            exitGraceMs: 500);

        var personTrack1 = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200);
        var personTrack99 = CreatePose(99, left: 120, top: 120, right: 220, bottom: 220);

        evaluator.Evaluate(CreateContext(100, [personTrack1], TestZones));
        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Intrusion, evaluator.CurrentState);

        // Track ID changes to 99 on next frame
        var res = evaluator.Evaluate(CreateContext(200, [personTrack99], TestZones));
        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Intrusion, evaluator.CurrentState);
        Assert.Empty(res.Events);
    }

    [Fact]
    public void PersonOutsideAssignedZoneDoesNotTriggerIntrusion()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: ["restricted-zone"],
            entryDelayMs: 0);

        // Person centroid at (800, 800) -> outside zone (0..500, 0..500)
        var personOutside = CreatePose(1, left: 750, top: 750, right: 850, bottom: 850);
        var context = CreateContext(100, [personOutside], TestZones);

        var result = evaluator.Evaluate(context);

        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Outside, evaluator.CurrentState);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void LowConfidenceDetectionsAreFilteredOut()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: ["restricted-zone"],
            confidenceThreshold: 0.80,
            entryDelayMs: 0);

        var lowConfidencePerson = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200, confidence: 0.50f);
        var result = evaluator.Evaluate(CreateContext(100, [lowConfidencePerson], TestZones));

        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Outside, evaluator.CurrentState);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void EmitSnapshotFalseOmitSnapshotMarker()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: ["restricted-zone"],
            entryDelayMs: 0,
            emitSnapshot: false);

        var person = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200);
        var result = evaluator.Evaluate(CreateContext(100, [person], TestZones));

        var startedEvent = Assert.Single(result.Events);
        Assert.Null(startedEvent.SnapshotJpeg);
    }

    [Fact]
    public void ResetClearsStateAndTimers()
    {
        var evaluator = new ZoneIntrusionAnalyticEvaluator(
            assignedZoneIds: ["restricted-zone"],
            entryDelayMs: 0);

        var person = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200);
        evaluator.Evaluate(CreateContext(100, [person], TestZones));
        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Intrusion, evaluator.CurrentState);

        evaluator.Reset();
        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Outside, evaluator.CurrentState);
    }
}
