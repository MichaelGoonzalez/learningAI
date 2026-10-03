using HandRaise.Domain.Zones;

namespace HandRaise.Domain.Tests;

public sealed class ZoneGeometryTests
{
    private static readonly Point2D[] Square =
    [
        new(0, 0),
        new(10, 0),
        new(10, 10),
        new(0, 10),
    ];

    [Fact]
    public void PointInsidePolygon()
    {
        Assert.True(ZoneGeometry.PointInPolygon(new Point2D(5, 5), Square));
    }

    [Fact]
    public void PointOutsidePolygon()
    {
        Assert.False(ZoneGeometry.PointInPolygon(new Point2D(15, 5), Square));
    }

    [Fact]
    public void PointOnEdgeIsInside()
    {
        Assert.True(ZoneGeometry.PointInPolygon(new Point2D(10, 5), Square));
    }

    [Fact]
    public void ConcavePolygonIsHandled()
    {
        Point2D[] polygon =
        [
            new(0, 0),
            new(10, 0),
            new(10, 10),
            new(5, 5),
            new(0, 10),
        ];

        Assert.True(ZoneGeometry.PointInPolygon(new Point2D(2, 5), polygon));
        Assert.False(ZoneGeometry.PointInPolygon(new Point2D(5, 8), polygon));
    }

    [Fact]
    public void FindZoneReturnsFirstMatchingName()
    {
        ZoneDefinition[] zones = [new("front", Square)];

        Assert.Equal("front", ZoneGeometry.FindZone(new Point2D(5, 5), zones));
    }

    [Fact]
    public void FindZoneWithoutZonesReturnsNull()
    {
        Assert.Null(ZoneGeometry.FindZone(new Point2D(5, 5), []));
    }

    [Fact]
    public void ZoneWithLessThanThreePointsFailsValidation()
    {
        var zone = new ZoneDefinition("invalid", [new(0, 0), new(1, 1)]);

        Assert.Throws<ArgumentException>(zone.Validate);
    }
}

