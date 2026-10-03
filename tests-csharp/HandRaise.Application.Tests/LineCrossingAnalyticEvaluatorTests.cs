using HandRaise.Application.Analytics;
using HandRaise.Application.Inference;
using HandRaise.Application.Tracking;
using HandRaise.Domain.Lines;
using HandRaise.Domain.Zones;
using Xunit;

namespace HandRaise.Application.Tests;

public class LineCrossingAnalyticEvaluatorTests
{
    private static readonly LineDefinition HorizontalLine = new(
        Id: "line-h",
        CameraId: "cam-1",
        Name: "Horizontal Tripwire",
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
    public void BidirectionalCrossing_EmitsLineCrossedEvent()
    {
        var evaluator = new LineCrossingAnalyticEvaluator(
            instanceId: "inst-lc-1",
            assignedLineIds: ["line-h"],
            confidenceThreshold: 0.50,
            cooldownMs: 500);

        // Frame 1: Person starts below line (Side A: y > 0.5)
        var pSideA = CreatePose(1, left: 450, top: 600, right: 550, bottom: 700);
        var res1 = evaluator.Evaluate(CreateContext(100, [pSideA], [HorizontalLine]));
        Assert.Empty(res1.Events);

        // Frame 2: Person moves above line (Side B: y < 0.5)
        var pSideB = CreatePose(1, left: 450, top: 200, right: 550, bottom: 300);
        var res2 = evaluator.Evaluate(CreateContext(200, [pSideB], [HorizontalLine]));

        Assert.Single(res2.Events);
        var ev = res2.Events[0];
        Assert.Equal("line_crossed", ev.EventType);
        Assert.Equal("line_crossing", ev.AnalyticType);
        Assert.Equal(1, ev.TrackId);
        Assert.Equal("a_to_b", ev.Metadata?["direction"]);
        Assert.Equal("line-h", ev.Metadata?["line_id"]);
    }

    [Fact]
    public void UnidirectionalLine_FiltersOppositeDirection()
    {
        var uniLine = HorizontalLine with { DirectionMode = LineDirectionMode.AToB };
        var evaluator = new LineCrossingAnalyticEvaluator(
            instanceId: "inst-lc-uni",
            assignedLineIds: ["line-h"],
            confidenceThreshold: 0.50);

        // Movement from Side B (y < 0.5) to Side A (y > 0.5) is b_to_a
        var pSideB = CreatePose(1, left: 450, top: 200, right: 550, bottom: 300);
        var pSideA = CreatePose(1, left: 450, top: 600, right: 550, bottom: 700);

        evaluator.Evaluate(CreateContext(100, [pSideB], [uniLine]));
        var res = evaluator.Evaluate(CreateContext(200, [pSideA], [uniLine]));

        // Should be ignored since line is strictly AToB
        Assert.Empty(res.Events);
    }
}

