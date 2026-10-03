using HandRaise.Application.Analytics;
using HandRaise.Application.Inference;
using HandRaise.Application.Tracking;
using HandRaise.Domain.Detection;
using HandRaise.Domain.Lines;
using HandRaise.Domain.Zones;
using Xunit;

namespace HandRaise.Application.Tests;

public class LineAnalyticsCoexistenceTests
{
    private static TrackedPose CreatePose(
        int trackId,
        float left = 100f,
        float top = 100f,
        float right = 200f,
        float bottom = 200f,
        float confidence = 0.90f)
    {
        var keypoints = Enumerable.Range(0, 17)
            .Select(i => new Keypoint((left + right) / 2f, (top + bottom) / 2f, 0.9f))
            .ToArray();

        return new TrackedPose(
            new PosePerson(new BoundingBox(left, top, right, bottom), confidence, 0, keypoints),
            trackId);
    }

    [Fact]
    public void FiveAnalytics_CoexistInSingleCameraPipeline_EvaluateWithoutError()
    {
        var line = new LineDefinition(
            Id: "test-line",
            CameraId: "cam-1",
            Name: "Test Line",
            PointA: new Point2D(0.0, 0.5),
            PointB: new Point2D(1.0, 0.5),
            DirectionMode: LineDirectionMode.Bidirectional,
            Enabled: true);

        var zone = new ZoneDefinition("test-zone", [new(0, 0), new(1000, 0), new(1000, 1000), new(0, 1000)]);

        var detectionOptions = new DetectionOptions
        {
            KeypointConfidence = 0.5,
            ShoulderMarginRatio = 0.15,
            StrictMode = false,
            StrictMarginPixels = 0.0,
            ConsecutiveFrames = 3,
            LowerConsecutiveFrames = 3,
            Cooldown = TimeSpan.FromMilliseconds(1000),
            TrackTimeToLive = TimeSpan.FromMilliseconds(5000)
        };

        var evaluators = new IAnalyticEvaluator[]
        {
            new HandRaiseAnalyticEvaluator(detectionOptions),
            new PersonPresenceAnalyticEvaluator(instanceId: "inst-presence", assignedZoneIds: ["test-zone"]),
            new ZoneIntrusionAnalyticEvaluator(instanceId: "inst-intrusion", assignedZoneIds: ["test-zone"], entryDelayMs: 0),
            new LineCrossingAnalyticEvaluator(instanceId: "inst-crossing", assignedLineIds: ["test-line"]),
            new PersonCountingAnalyticEvaluator(instanceId: "inst-counting", assignedLineIds: ["test-line"])
        };

        var personF1 = CreatePose(1, left: 400, top: 100, right: 600, bottom: 300);
        var updateF1 = new TrackerUpdate([personF1], new HashSet<int> { 1 });
        var contextF1 = new AnalyticFrameContext(
            cameraId: "cam-1",
            timestampMilliseconds: 100,
            timestampUtc: DateTimeOffset.UtcNow,
            frameWidth: 1000,
            frameHeight: 1000,
            trackingUpdate: updateF1,
            zones: [zone],
            lines: [line]);

        foreach (var evaluator in evaluators)
        {
            var res = evaluator.Evaluate(contextF1);
            Assert.NotNull(res);
        }

        // Frame 2: Person moves across line
        var personF2 = CreatePose(1, left: 400, top: 600, right: 600, bottom: 700);
        var updateF2 = new TrackerUpdate([personF2], new HashSet<int> { 1 });
        var contextF2 = new AnalyticFrameContext(
            cameraId: "cam-1",
            timestampMilliseconds: 200,
            timestampUtc: DateTimeOffset.UtcNow,
            frameWidth: 1000,
            frameHeight: 1000,
            trackingUpdate: updateF2,
            zones: [zone],
            lines: [line]);

        var allEvents = new List<Domain.Analytics.AnalyticEvent>();
        foreach (var evaluator in evaluators)
        {
            var res = evaluator.Evaluate(contextF2);
            allEvents.AddRange(res.Events);
        }

        // LineCrossing and PersonCounting should have fired crossing/count events
        Assert.Contains(allEvents, e => e.EventType == "line_crossed");
        Assert.Contains(allEvents, e => e.EventType == "person_count_updated");
    }
}

