using HandRaise.Domain.Lines;
using HandRaise.Domain.Zones;
using Xunit;

namespace HandRaise.Domain.Tests;

public class LineGeometryTests
{
    [Fact]
    public void DetermineSide_IdentifiesSidesAndHysteresisMargin()
    {
        // Horizontal line from (0.0, 0.5) to (1.0, 0.5)
        var a = new Point2D(0.0, 0.5);
        var b = new Point2D(1.0, 0.5);

        // Point with y > 0.5 is on Side A
        var pAbove = new Point2D(0.5, 0.6);
        Assert.Equal(LineSide.SideA, LineGeometry.DetermineSide(pAbove, a, b));

        // Point with y < 0.5 is on Side B
        var pBelow = new Point2D(0.5, 0.4);
        Assert.Equal(LineSide.SideB, LineGeometry.DetermineSide(pBelow, a, b));

        // Point within margin (default 0.015) is OnLine
        var pMargin = new Point2D(0.5, 0.505);
        Assert.Equal(LineSide.OnLine, LineGeometry.DetermineSide(pMargin, a, b));
    }

    [Fact]
    public void SignedDistance_CalculatesPerpendicularDistanceCorrectly()
    {
        var a = new Point2D(0.0, 0.5);
        var b = new Point2D(1.0, 0.5);

        var p1 = new Point2D(0.5, 0.7);
        Assert.Equal(0.2, LineGeometry.SignedDistance(p1, a, b), precision: 4);

        var p2 = new Point2D(0.5, 0.3);
        Assert.Equal(-0.2, LineGeometry.SignedDistance(p2, a, b), precision: 4);
    }

    [Fact]
    public void ProjectionParameter_CalculatesRelativeTAlongSegment()
    {
        var a = new Point2D(0.0, 0.0);
        var b = new Point2D(1.0, 0.0);

        var pStart = new Point2D(0.0, 0.5);
        Assert.Equal(0.0, LineGeometry.ProjectionParameter(pStart, a, b), precision: 4);

        var pMid = new Point2D(0.5, 0.5);
        Assert.Equal(0.5, LineGeometry.ProjectionParameter(pMid, a, b), precision: 4);

        var pEnd = new Point2D(1.0, 0.5);
        Assert.Equal(1.0, LineGeometry.ProjectionParameter(pEnd, a, b), precision: 4);
    }

    [Fact]
    public void SegmentsIntersect_CorrectlyDetectsCrossings()
    {
        // Line 1: (0.0, 0.5) to (1.0, 0.5)
        var p1 = new Point2D(0.0, 0.5);
        var p2 = new Point2D(1.0, 0.5);

        // Crossing trajectory: (0.5, 0.3) to (0.5, 0.7)
        var q1 = new Point2D(0.5, 0.3);
        var q2 = new Point2D(0.5, 0.7);

        Assert.True(LineGeometry.SegmentsIntersect(p1, p2, q1, q2));

        // Non-crossing trajectory
        var qNonCross1 = new Point2D(0.5, 0.1);
        var qNonCross2 = new Point2D(0.5, 0.4);
        Assert.False(LineGeometry.SegmentsIntersect(p1, p2, qNonCross1, qNonCross2));
    }

    [Fact]
    public void LineDefinition_Validate_EnforcesValidCoordinates()
    {
        var valid = new LineDefinition(
            Id: "line-1",
            CameraId: "cam-1",
            Name: "Valid Line",
            PointA: new Point2D(0.1, 0.1),
            PointB: new Point2D(0.9, 0.9));

        valid.Validate(); // Should not throw

        var invalidA = valid with { PointA = new Point2D(-0.1, 0.5) };
        Assert.Throws<ArgumentException>(() => invalidA.Validate());

        var invalidB = valid with { PointB = new Point2D(0.5, 1.5) };
        Assert.Throws<ArgumentException>(() => invalidB.Validate());

        var invalidEmptyId = valid with { Id = "" };
        Assert.Throws<ArgumentException>(() => invalidEmptyId.Validate());

        var invalidEmptyName = valid with { Name = " " };
        Assert.Throws<ArgumentException>(() => invalidEmptyName.Validate());

        var invalidIdentical = valid with { PointB = valid.PointA };
        Assert.Throws<ArgumentException>(() => invalidIdentical.Validate());
    }
}

