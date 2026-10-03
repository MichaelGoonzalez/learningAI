using System.Diagnostics;
using HandRaise.Application.Capture;
using HandRaise.Application.Overlay;
using HandRaise.Infrastructure.Windows.Overlay;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class OpenCvOverlayRendererTests
{
    [Fact]
    public void Draw_WithNoPeople_ReturnsAnnotatedCopyAndPreservesInput()
    {
        var original = Enumerable.Repeat((byte)20, 320 * 240 * 3).ToArray();
        using var source = new VideoFrame(
            320, 240, original.ToArray(), 0, 0, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        var renderer = new OpenCvOverlayRenderer(0.5);

        using var output = renderer.Draw(
            source,
            [],
            [],
            new OverlayStatistics(30, 12, "CPU", "CPUExecutionProvider", "YOLO26n Pose"));

        Assert.Equal(original, source.Pixels.ToArray());
        Assert.Equal(original.Length, output.Pixels.Length);
        Assert.NotEqual(original, output.Pixels.ToArray());
    }
}
