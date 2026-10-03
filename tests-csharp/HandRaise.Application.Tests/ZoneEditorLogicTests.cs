using HandRaise.Application.Zones;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Tests;

public sealed class ZoneEditorLogicTests
{
    [Fact]
    public void NormalizedAndPixelCoordinatesRoundTrip()
    {
        NormalizedZone[] source = [new("entrada", [new(0.1, 0.2), new(0.8, 0.2), new(0.5, 0.9)])];

        var pixels = ZoneEditorLogic.ToPixels(source, 1280, 720);
        var result = ZoneEditorLogic.ToNormalized(pixels, 1280, 720);

        Assert.Equal(source[0].Points, result[0].Points);
        Assert.Equal(128, pixels[0].Points[0].X);
        Assert.Equal(144, pixels[0].Points[0].Y);
    }

    [Fact]
    public void RejectsDegeneratePolygon()
    {
        NormalizedZone[] zones = [new("línea", [new(0.1, 0.1), new(0.2, 0.2), new(0.3, 0.3)])];
        Assert.Throws<ArgumentException>(() => ZoneEditorLogic.Validate(zones));
    }

    [Fact]
    public void RejectsSelfIntersection()
    {
        NormalizedZone[] zones = [new("cruzada", [new(0.1, 0.1), new(0.9, 0.9), new(0.9, 0.1), new(0.1, 0.9)])];
        Assert.Throws<ArgumentException>(() => ZoneEditorLogic.Validate(zones));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsEmptyName(string name)
    {
        NormalizedZone[] zones = [new(name, [new(0.1, 0.1), new(0.9, 0.1), new(0.5, 0.9)])];
        Assert.Throws<ArgumentException>(() => ZoneEditorLogic.Validate(zones));
    }

    [Fact]
    public void RejectsDuplicateNamesIgnoringCase()
    {
        NormalizedPoint[] points = [new(0.1, 0.1), new(0.9, 0.1), new(0.5, 0.9)];
        NormalizedZone[] zones = [new("Sala", points), new("sala", points)];
        Assert.Throws<ArgumentException>(() => ZoneEditorLogic.Validate(zones));
    }
}
