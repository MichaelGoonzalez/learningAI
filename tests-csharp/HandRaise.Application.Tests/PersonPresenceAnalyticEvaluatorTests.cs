using HandRaise.Application.Analytics;
using HandRaise.Application.Inference;
using HandRaise.Application.Tracking;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Detection;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Tests;

public sealed class PersonPresenceAnalyticEvaluatorTests
{
    private static TrackedPose CreatePerson(
        int trackId,
        float confidence = 0.85f,
        float left = 100,
        float top = 100,
        float right = 200,
        float bottom = 200)
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

    [Fact]
    public void InitialStateIsAbsent()
    {
        var evaluator = new PersonPresenceAnalyticEvaluator("inst-1");
        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Absent, evaluator.CurrentState);
    }

    [Fact]
    public void PersonDetectedTransitionsFromAbsentToCandidate()
    {
        var evaluator = new PersonPresenceAnalyticEvaluator("inst-1", minPresenceMs: 1000);
        var context = CreateContext(0, [CreatePerson(1)]);

        var result = evaluator.Evaluate(context);

        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Candidate, evaluator.CurrentState);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void CandidateDoesNotEmitBeforeMinPresenceMs()
    {
        var evaluator = new PersonPresenceAnalyticEvaluator("inst-1", minPresenceMs: 1000);

        evaluator.Evaluate(CreateContext(0, [CreatePerson(1)]));
        var result500 = evaluator.Evaluate(CreateContext(500, [CreatePerson(1)]));

        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Candidate, evaluator.CurrentState);
        Assert.Empty(result500.Events);
    }

    [Fact]
    public void CandidateTransitionsToPresentAndEmitsStartedOnce()
    {
        var evaluator = new PersonPresenceAnalyticEvaluator("inst-1", minPresenceMs: 1000);

        evaluator.Evaluate(CreateContext(0, [CreatePerson(1)]));
        evaluator.Evaluate(CreateContext(500, [CreatePerson(1)]));
        var result1000 = evaluator.Evaluate(CreateContext(1000, [CreatePerson(1)]));

        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Present, evaluator.CurrentState);
        Assert.Single(result1000.Events);

        var evt = result1000.Events[0];
        Assert.Equal("person_presence_started", evt.EventType);
        Assert.Equal("person_presence", evt.AnalyticType);
        Assert.Equal("inst-1", evt.AnalyticInstanceId);
        Assert.Equal(1, evt.TrackId);
        Assert.Equal(1, Convert.ToInt32(evt.Metadata!["person_count"]));

        // Frame at 1500 with same person should not emit duplicate event
        var result1500 = evaluator.Evaluate(CreateContext(1500, [CreatePerson(1)]));
        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Present, evaluator.CurrentState);
        Assert.Empty(result1500.Events);
    }

    [Fact]
    public void AbsenceShorterThanGracePeriodDoesNotEmitEnded()
    {
        var evaluator = new PersonPresenceAnalyticEvaluator("inst-1", minPresenceMs: 500, absenceGraceMs: 1500);

        evaluator.Evaluate(CreateContext(0, [CreatePerson(1)]));
        evaluator.Evaluate(CreateContext(500, [CreatePerson(1)])); // Started

        // Person disappears at 1000 (elapsed absence: 0ms)
        var result1000 = evaluator.Evaluate(CreateContext(1000, []));
        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Present, evaluator.CurrentState);
        Assert.Empty(result1000.Events);

        // Person still gone at 2000 (elapsed absence: 1000ms < 1500ms grace)
        var result2000 = evaluator.Evaluate(CreateContext(2000, []));
        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Present, evaluator.CurrentState);
        Assert.Empty(result2000.Events);

        // Person reappears at 2200
        var result2200 = evaluator.Evaluate(CreateContext(2200, [CreatePerson(1)]));
        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Present, evaluator.CurrentState);
        Assert.Empty(result2200.Events);
    }

    [Fact]
    public void AbsenceExceedingGracePeriodEmitsEndedOnce()
    {
        var evaluator = new PersonPresenceAnalyticEvaluator("inst-1", minPresenceMs: 500, absenceGraceMs: 1000);

        evaluator.Evaluate(CreateContext(0, [CreatePerson(1)]));
        evaluator.Evaluate(CreateContext(500, [CreatePerson(1)])); // Started

        evaluator.Evaluate(CreateContext(1000, [])); // Absence begins
        var result2000 = evaluator.Evaluate(CreateContext(2000, [])); // Absence = 1000ms >= 1000ms

        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Absent, evaluator.CurrentState);
        Assert.Single(result2000.Events);

        var endedEvent = result2000.Events[0];
        Assert.Equal("person_presence_ended", endedEvent.EventType);
        Assert.Equal("person_presence", endedEvent.AnalyticType);
        Assert.True(Convert.ToInt64(endedEvent.Metadata!["presence_duration_ms"]) >= 1500);

        // Following frame while still absent should NOT emit another ended
        var result3000 = evaluator.Evaluate(CreateContext(3000, []));
        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Absent, evaluator.CurrentState);
        Assert.Empty(result3000.Events);
    }

    [Fact]
    public void ReentryAfterEndedAllowsNewStarted()
    {
        var evaluator = new PersonPresenceAnalyticEvaluator("inst-1", minPresenceMs: 500, absenceGraceMs: 500);

        evaluator.Evaluate(CreateContext(0, [CreatePerson(1)]));
        evaluator.Evaluate(CreateContext(500, [CreatePerson(1)])); // Started 1
        evaluator.Evaluate(CreateContext(1000, []));
        evaluator.Evaluate(CreateContext(1500, [])); // Ended 1

        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Absent, evaluator.CurrentState);

        // Reentry at 3000
        evaluator.Evaluate(CreateContext(3000, [CreatePerson(2)]));
        var result3500 = evaluator.Evaluate(CreateContext(3500, [CreatePerson(2)])); // Started 2

        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Present, evaluator.CurrentState);
        Assert.Single(result3500.Events);
        Assert.Equal("person_presence_started", result3500.Events[0].EventType);
        Assert.Equal(2, result3500.Events[0].TrackId);
    }

    [Fact]
    public void MultiplePeopleAndTrackHandoffMaintainPresentState()
    {
        var evaluator = new PersonPresenceAnalyticEvaluator("inst-1", minPresenceMs: 500, absenceGraceMs: 1000);

        evaluator.Evaluate(CreateContext(0, [CreatePerson(1)]));
        evaluator.Evaluate(CreateContext(500, [CreatePerson(1), CreatePerson(2)])); // Started with 2 people

        // Person 1 leaves, Person 2 remains
        var result1000 = evaluator.Evaluate(CreateContext(1000, [CreatePerson(2)]));
        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Present, evaluator.CurrentState);
        Assert.Empty(result1000.Events);

        // Person 2 replaced by Person 3 directly
        var result1500 = evaluator.Evaluate(CreateContext(1500, [CreatePerson(3)]));
        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Present, evaluator.CurrentState);
        Assert.Empty(result1500.Events);
    }

    [Fact]
    public void AssignedZonesFilterPersonsCorrectly()
    {
        var zoneA = new ZoneDefinition("zone-a", [new(0, 0), new(500, 0), new(500, 500), new(0, 500)]);
        var zoneB = new ZoneDefinition("zone-b", [new(600, 0), new(1000, 0), new(1000, 500), new(600, 500)]);
        var zones = new[] { zoneA, zoneB };

        var evaluator = new PersonPresenceAnalyticEvaluator("inst-zone-a", assignedZoneIds: ["zone-a"], minPresenceMs: 0);

        // Person in zone-b (center at 700, 200)
        var personInB = CreatePerson(1, left: 650, top: 150, right: 750, bottom: 250);
        var resultB = evaluator.Evaluate(CreateContext(0, [personInB], zones));
        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Absent, evaluator.CurrentState);
        Assert.Empty(resultB.Events);

        // Person in zone-a (center at 200, 200)
        var personInA = CreatePerson(2, left: 150, top: 150, right: 250, bottom: 250);
        var resultA = evaluator.Evaluate(CreateContext(100, [personInA], zones));
        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Present, evaluator.CurrentState);
        Assert.Single(resultA.Events);
        Assert.Equal("zone-a", resultA.Events[0].ZoneId);
    }

    [Fact]
    public void GlobalEvaluationWithoutZonesConsidersAllPersons()
    {
        var evaluator = new PersonPresenceAnalyticEvaluator("inst-global", assignedZoneIds: [], minPresenceMs: 0);
        var person = CreatePerson(1, left: 800, top: 800, right: 900, bottom: 900);

        var result = evaluator.Evaluate(CreateContext(0, [person]));
        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Present, evaluator.CurrentState);
        Assert.Single(result.Events);
    }

    [Fact]
    public void ConfidenceThresholdFiltersLowConfidenceDetections()
    {
        var evaluator = new PersonPresenceAnalyticEvaluator("inst-conf", confidenceThreshold: 0.70, minPresenceMs: 0);

        var lowConfPerson = CreatePerson(1, confidence: 0.50f);
        var resultLow = evaluator.Evaluate(CreateContext(0, [lowConfPerson]));
        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Absent, evaluator.CurrentState);
        Assert.Empty(resultLow.Events);

        var highConfPerson = CreatePerson(2, confidence: 0.80f);
        var resultHigh = evaluator.Evaluate(CreateContext(100, [highConfPerson]));
        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Present, evaluator.CurrentState);
        Assert.Single(resultHigh.Events);
    }

    [Fact]
    public void EmitSnapshotFlagIsReflectedInMetadata()
    {
        var evaluatorNoSnap = new PersonPresenceAnalyticEvaluator("inst-no-snap", emitSnapshot: false, minPresenceMs: 0);
        var result = evaluatorNoSnap.Evaluate(CreateContext(0, [CreatePerson(1)]));

        Assert.Single(result.Events);
        Assert.False((bool)result.Events[0].Metadata!["emit_snapshot"]!);
    }
}

