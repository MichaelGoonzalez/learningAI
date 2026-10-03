using HandRaise.Application.Analytics;
using HandRaise.Application.Inference;
using HandRaise.Application.Tracking;
using HandRaise.Domain.Detection;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Tests;

public class ZoneIntrusionCoexistenceTests
{
    private static TrackedPose CreatePose(
        int trackId,
        float left = 100f,
        float top = 100f,
        float right = 200f,
        float bottom = 200f,
        float confidence = 0.90f,
        bool rightHandRaised = false)
    {
        var keypoints = new Keypoint[17];
        var shoulderY = 150.0;
        var wristY = rightHandRaised ? 50.0 : 250.0;

        for (int i = 0; i < 17; i++)
        {
            keypoints[i] = new Keypoint(150, 150, 0.9f);
        }
        // Right shoulder = 6, Right wrist = 10
        keypoints[6] = new Keypoint(160, shoulderY, 0.9f);
        keypoints[10] = new Keypoint(160, wristY, 0.9f);

        return new TrackedPose(
            new PosePerson(new BoundingBox(left, top, right, bottom), confidence, 0, keypoints),
            trackId);
    }

    [Fact]
    public void TwoIntrusionInstancesOnDistinctZonesMaintainIsolatedStates()
    {
        var zoneA = new ZoneDefinition("area-a", [new(0, 0), new(400, 0), new(400, 400), new(0, 400)]);
        var zoneB = new ZoneDefinition("area-b", [new(600, 0), new(1000, 0), new(1000, 400), new(600, 400)]);
        var zones = new[] { zoneA, zoneB };

        var instanceA = new ZoneIntrusionAnalyticEvaluator("inst-a", assignedZoneIds: ["area-a"], entryDelayMs: 0);
        var instanceB = new ZoneIntrusionAnalyticEvaluator("inst-b", assignedZoneIds: ["area-b"], entryDelayMs: 0);

        // Person strictly inside Area A (center at 150, 150)
        var personInA = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200);

        var update = new TrackerUpdate([personInA], new HashSet<int> { 1 });
        var context = new AnalyticFrameContext("cam-1", 100, DateTimeOffset.UtcNow, 1920, 1080, update, zones);

        var resultA = instanceA.Evaluate(context);
        var resultB = instanceB.Evaluate(context);

        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Intrusion, instanceA.CurrentState);
        var evtA = Assert.Single(resultA.Events);
        Assert.Equal("inst-a", evtA.AnalyticInstanceId);
        Assert.Equal("area-a", evtA.ZoneId);

        Assert.Equal(ZoneIntrusionAnalyticEvaluator.IntrusionState.Outside, instanceB.CurrentState);
        Assert.Empty(resultB.Events);
    }

    [Fact]
    public void TriAnalyticsCoexistenceEvaluatesInSingleFrameWithoutConflict()
    {
        var zone = new ZoneDefinition("secure-vault", [new(0, 0), new(500, 0), new(500, 500), new(0, 500)]);
        var zones = new[] { zone };

        var handOptions = new DetectionOptions
        {
            KeypointConfidence = 0.5,
            ShoulderMarginRatio = 0.15,
            StrictMode = false,
            StrictMarginPixels = 0,
            ConsecutiveFrames = 1,
            LowerConsecutiveFrames = 1,
            Cooldown = TimeSpan.FromMilliseconds(100),
            TrackTimeToLive = TimeSpan.FromMilliseconds(5000)
        };

        var handRaise = new HandRaiseAnalyticEvaluator(handOptions, "hr-inst");
        var presence = new PersonPresenceAnalyticEvaluator("presence-inst", assignedZoneIds: ["secure-vault"], minPresenceMs: 0);
        var intrusion = new ZoneIntrusionAnalyticEvaluator("intrusion-inst", assignedZoneIds: ["secure-vault"], entryDelayMs: 0);

        // Person in secure-vault with right hand raised
        var person = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200, rightHandRaised: true);
        var update = new TrackerUpdate([person], new HashSet<int> { 1 });
        var context = new AnalyticFrameContext("cam-1", 100, DateTimeOffset.UtcNow, 1920, 1080, update, zones);

        var hrResult = handRaise.Evaluate(context);
        var presResult = presence.Evaluate(context);
        var intrResult = intrusion.Evaluate(context);

        // HandRaise emits legacy hand_raised
        Assert.Single(hrResult.LegacyEvents);
        Assert.Equal("hand_raised", hrResult.LegacyEvents[0].Type);

        // Presence emits person_presence_started
        Assert.Single(presResult.Events);
        Assert.Equal("person_presence_started", presResult.Events[0].EventType);

        // Intrusion emits zone_intrusion_started
        Assert.Single(intrResult.Events);
        Assert.Equal("zone_intrusion_started", intrResult.Events[0].EventType);
        Assert.Equal("secure-vault", intrResult.Events[0].ZoneId);
    }

    [Fact]
    public void DisablingIntrusionLeavesPresenceAndHandRaiseOperatingNormally()
    {
        var zone = new ZoneDefinition("secure-vault", [new(0, 0), new(500, 0), new(500, 500), new(0, 500)]);
        var zones = new[] { zone };

        var handOptions = new DetectionOptions
        {
            KeypointConfidence = 0.5,
            ShoulderMarginRatio = 0.15,
            StrictMode = false,
            StrictMarginPixels = 0,
            ConsecutiveFrames = 1,
            LowerConsecutiveFrames = 1,
            Cooldown = TimeSpan.FromMilliseconds(100),
            TrackTimeToLive = TimeSpan.FromMilliseconds(5000)
        };

        var handRaise = new HandRaiseAnalyticEvaluator(handOptions, "hr-inst");
        var presence = new PersonPresenceAnalyticEvaluator("presence-inst", assignedZoneIds: ["secure-vault"], minPresenceMs: 0);
        var intrusion = new ZoneIntrusionAnalyticEvaluator("intrusion-inst", assignedZoneIds: ["secure-vault"], entryDelayMs: 0)
        {
            IsEnabled = false
        };

        var person = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200, rightHandRaised: true);
        var update = new TrackerUpdate([person], new HashSet<int> { 1 });
        var context = new AnalyticFrameContext("cam-1", 100, DateTimeOffset.UtcNow, 1920, 1080, update, zones);

        var hrResult = handRaise.Evaluate(context);
        var presResult = presence.Evaluate(context);
        var intrResult = intrusion.Evaluate(context);

        Assert.Single(hrResult.LegacyEvents);
        Assert.Single(presResult.Events);
        Assert.Empty(intrResult.Events);
    }
}
