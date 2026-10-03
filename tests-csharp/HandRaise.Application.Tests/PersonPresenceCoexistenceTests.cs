using HandRaise.Application.Analytics;
using HandRaise.Application.Capture;
using HandRaise.Application.Inference;
using HandRaise.Application.Overlay;
using HandRaise.Application.Pipelines;
using HandRaise.Application.Tracking;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Detection;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Tests;

public sealed class PersonPresenceCoexistenceTests
{
    private static TrackedPose CreatePose(
        int trackId,
        float left = 100,
        float top = 100,
        float right = 200,
        float bottom = 200,
        bool rightHandRaised = false)
    {
        // 17 keypoints standard
        var keypoints = new List<Keypoint>();
        for (var i = 0; i < 17; i++)
        {
            keypoints.Add(new Keypoint(150, 150, 0.9f));
        }

        // Keypoint 6: right shoulder at (160, 120)
        keypoints[6] = new Keypoint(160, 120, 0.9f);

        // Keypoint 10: right wrist
        keypoints[10] = rightHandRaised
            ? new Keypoint(160, 60, 0.95f) // Above shoulder -> Hand raised
            : new Keypoint(160, 180, 0.95f); // Below shoulder -> Hand lowered

        return new TrackedPose(
            new PosePerson(new BoundingBox(left, top, right, bottom), 0.9f, 0, keypoints),
            trackId);
    }

    [Fact]
    public void TwoInstancesOnSameCameraMaintainIndependentState()
    {
        var zoneA = new ZoneDefinition("zone-a", [new(0, 0), new(500, 0), new(500, 500), new(0, 500)]);
        var zoneB = new ZoneDefinition("zone-b", [new(600, 0), new(1000, 0), new(1000, 500), new(600, 500)]);
        var zones = new[] { zoneA, zoneB };

        var instanceA = new PersonPresenceAnalyticEvaluator("inst-a", assignedZoneIds: ["zone-a"], minPresenceMs: 0);
        var instanceB = new PersonPresenceAnalyticEvaluator("inst-b", assignedZoneIds: ["zone-b"], minPresenceMs: 0);

        var personInA = CreatePose(1, left: 100, top: 100, right: 200, bottom: 200);

        var update = new TrackerUpdate([personInA], new HashSet<int> { 1 });
        var context = new AnalyticFrameContext("cam-1", 0, DateTimeOffset.UtcNow, 1920, 1080, update, zones);

        var resultA = instanceA.Evaluate(context);
        var resultB = instanceB.Evaluate(context);

        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Present, instanceA.CurrentState);
        Assert.Single(resultA.Events);
        Assert.Equal("inst-a", resultA.Events[0].AnalyticInstanceId);

        Assert.Equal(PersonPresenceAnalyticEvaluator.PresenceState.Absent, instanceB.CurrentState);
        Assert.Empty(resultB.Events);
    }

    [Fact]
    public void HandRaiseAndPersonPresenceCoexistInMultiEvaluatorContext()
    {
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

        var handRaiseEvaluator = new HandRaiseAnalyticEvaluator(handOptions, "hr-inst");
        var presenceEvaluator = new PersonPresenceAnalyticEvaluator("presence-inst", minPresenceMs: 0);

        var personWithHandRaised = CreatePose(1, rightHandRaised: true);
        var update = new TrackerUpdate([personWithHandRaised], new HashSet<int> { 1 });
        var context = new AnalyticFrameContext("cam-1", 0, DateTimeOffset.UtcNow, 1920, 1080, update, []);

        var hrResult = handRaiseEvaluator.Evaluate(context);
        var presenceResult = presenceEvaluator.Evaluate(context);

        Assert.Single(hrResult.LegacyEvents);
        Assert.Equal("hand_raised", hrResult.LegacyEvents[0].Type);

        Assert.Single(presenceResult.Events);
        Assert.Equal("person_presence_started", presenceResult.Events[0].EventType);
    }

    [Fact]
    public void DisablingPersonPresenceLeavesHandRaiseOperatingNormally()
    {
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

        var handRaiseEvaluator = new HandRaiseAnalyticEvaluator(handOptions, "hr-inst");
        var presenceEvaluator = new PersonPresenceAnalyticEvaluator("presence-inst", minPresenceMs: 0)
        {
            IsEnabled = false
        };

        var personWithHandRaised = CreatePose(1, rightHandRaised: true);
        var update = new TrackerUpdate([personWithHandRaised], new HashSet<int> { 1 });
        var context = new AnalyticFrameContext("cam-1", 0, DateTimeOffset.UtcNow, 1920, 1080, update, []);

        var hrResult = handRaiseEvaluator.Evaluate(context);
        var presenceResult = presenceEvaluator.Evaluate(context);

        Assert.Single(hrResult.LegacyEvents);
        Assert.Empty(presenceResult.Events);
    }
}

