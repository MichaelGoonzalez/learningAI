using HandRaise.Application.Analytics;
using HandRaise.Application.Inference;
using HandRaise.Application.Tracking;
using HandRaise.Domain.Lines;
using HandRaise.Domain.Zones;
using Xunit;

namespace HandRaise.Application.Tests;

public class PersonCountingAnalyticEvaluatorTests
{
    private static readonly LineDefinition EntranceLine = new(
        Id: "line-in",
        CameraId: "cam-1",
        Name: "Entrance Line",
        PointA: new Point2D(0.0, 0.5),
        PointB: new Point2D(1.0, 0.5),
        DirectionMode: LineDirectionMode.Bidirectional,
        Enabled: true);

    private static TrackedPose CreatePose(
        int trackId,
        float left = 450f,
        float top = 400f,
        float right = 550f,
        float bottom = 500f,
        float confidence = 0.90f)
    {
        return new TrackedPose(
            new PosePerson(new BoundingBox(left, top, right, bottom), confidence, 0, []),
            trackId);
    }

    private static AnalyticFrameContext CreateContext(
        double timestampMs,
        IReadOnlyList<TrackedPose> poses,
        IReadOnlyList<LineDefinition> lines,
        int width = 1000,
        int height = 1000)
    {
        var activeIds = poses.Select(p => p.TrackId.GetValueOrDefault()).ToHashSet();
        var update = new TrackerUpdate(poses, activeIds);
        return new AnalyticFrameContext(
            cameraId: "cam-1",
            timestampMilliseconds: timestampMs,
            timestampUtc: DateTimeOffset.UtcNow,
            frameWidth: width,
            frameHeight: height,
            trackingUpdate: update,
            zones: [],
            lines: lines);
    }

    [Fact]
    public void EntryAndExit_UpdatesOccupancyCorrectly()
    {
        var evaluator = new PersonCountingAnalyticEvaluator(
            instanceId: "inst-pc-1",
            assignedLineIds: ["line-in"],
            confidenceThreshold: 0.50,
            entryDirection: "a_to_b",
            initialOccupancy: 10,
            minimumOccupancy: 0);

        Assert.Equal(10, evaluator.CurrentOccupancy);
        Assert.Equal(0, evaluator.Entries);
        Assert.Equal(0, evaluator.Exits);

        // Entry: Track 1 crosses Side A -> Side B
        var p1SideA = CreatePose(1, left: 450, top: 600, right: 550, bottom: 700);
        var p1SideB = CreatePose(1, left: 450, top: 200, right: 550, bottom: 300);

        evaluator.Evaluate(CreateContext(100, [p1SideA], [EntranceLine]));
        var res1 = evaluator.Evaluate(CreateContext(200, [p1SideB], [EntranceLine]));

        Assert.Single(res1.Events);
        Assert.Equal("person_count_updated", res1.Events[0].EventType);
        Assert.Equal(1, evaluator.Entries);
        Assert.Equal(0, evaluator.Exits);
        Assert.Equal(11, evaluator.CurrentOccupancy);
    }

    [Fact]
    public void MaxOccupancyThresholdReached_EmitsEventAndRearms()
    {
        var evaluator = new PersonCountingAnalyticEvaluator(
            assignedLineIds: ["line-in"],
            entryDirection: "a_to_b",
            initialOccupancy: 1,
            maximumOccupancy: 2);

        // Initial occupancy is 1. One entry (Side A -> Side B) raises it to 2 (max)
        var p1SideA = CreatePose(1, left: 450, top: 600, right: 550, bottom: 700);
        var p1SideB = CreatePose(1, left: 450, top: 200, right: 550, bottom: 300);

        evaluator.Evaluate(CreateContext(100, [p1SideA], [EntranceLine]));
        var res1 = evaluator.Evaluate(CreateContext(200, [p1SideB], [EntranceLine]));

        // Should contain person_count_updated AND occupancy_threshold_reached
        Assert.Equal(2, res1.Events.Count);
        Assert.Contains(res1.Events, e => e.EventType == "person_count_updated");
        Assert.Contains(res1.Events, e => e.EventType == "occupancy_threshold_reached");

        // Another entry: occupancy becomes 3. Threshold was already reached, so no duplicate alert
        var p2SideA = CreatePose(2, left: 450, top: 600, right: 550, bottom: 700);
        var p2SideB = CreatePose(2, left: 450, top: 200, right: 550, bottom: 300);
        evaluator.Evaluate(CreateContext(1500, [p2SideA], [EntranceLine]));
        var res2 = evaluator.Evaluate(CreateContext(1600, [p2SideB], [EntranceLine]));

        Assert.Single(res2.Events); // only person_count_updated

        // Exit lowers occupancy back to 2, then another exit lowers to 1 (< max 2) -> Rearms!
        evaluator.Evaluate(CreateContext(3000, [p1SideB], [EntranceLine]));
        evaluator.Evaluate(CreateContext(3100, [p1SideA], [EntranceLine])); // occupancy = 2
        evaluator.Evaluate(CreateContext(4500, [p2SideB], [EntranceLine]));
        evaluator.Evaluate(CreateContext(4600, [p2SideA], [EntranceLine])); // occupancy = 1 (< 2 -> rearmed!)

        // Next entry reaches 2 again -> triggers occupancy_threshold_reached again
        var p3SideA = CreatePose(3, left: 450, top: 600, right: 550, bottom: 700);
        var p3SideB = CreatePose(3, left: 450, top: 200, right: 550, bottom: 300);
        evaluator.Evaluate(CreateContext(6000, [p3SideA], [EntranceLine]));
        var res3 = evaluator.Evaluate(CreateContext(6100, [p3SideB], [EntranceLine]));

        Assert.Equal(2, res3.Events.Count);
        Assert.Contains(res3.Events, e => e.EventType == "occupancy_threshold_reached");
    }
}

