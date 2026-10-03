using HandRaise.Domain.Detection;

namespace HandRaise.Domain.Tests;

public sealed class RaiseHandTrackerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly HandsState Up = new(true, false, 0.9);
    private static readonly HandsState Down = new(false, false, 0);

    [Fact]
    public void OneOrTwoFrameBlinkDoesNotEmit()
    {
        var tracker = new RaiseHandTracker(TestData.Options());
        var events = new List<RaiseEvent>();

        events.AddRange(tracker.Update(1, Up, At(0)));
        events.AddRange(tracker.Update(1, Up, At(10)));
        events.AddRange(tracker.Update(1, Down, At(20)));

        Assert.Empty(events);
    }

    [Fact]
    public void ConfiguredFramesEmitExactlyOneRaisedEvent()
    {
        var tracker = new RaiseHandTracker(TestData.Options());
        var events = new List<RaiseEvent>();

        for (var frame = 0; frame < 3; frame++)
        {
            events.AddRange(tracker.Update(1, Up, At(frame * 10)));
        }

        var raised = Assert.Single(events);
        Assert.Equal(RaiseEventType.HandRaised, raised.Type);
        Assert.Equal(HandSide.Left, raised.Hand);
    }

    [Fact]
    public void HoldingHandUpDoesNotDuplicate()
    {
        var tracker = new RaiseHandTracker(TestData.Options());
        var events = new List<RaiseEvent>();

        for (var frame = 0; frame < 10; frame++)
        {
            events.AddRange(tracker.Update(1, Up, At(frame * 10)));
        }

        Assert.Single(events);
        Assert.Equal(RaiseEventType.HandRaised, events[0].Type);
    }

    [Fact]
    public void LoweringEmitsAfterConfiguredFrames()
    {
        var tracker = new RaiseHandTracker(TestData.Options());
        for (var frame = 0; frame < 3; frame++)
        {
            tracker.Update(1, Up, At(frame * 10));
        }

        Assert.Empty(tracker.Update(1, Down, At(30)));
        var lowered = Assert.Single(tracker.Update(1, Down, At(40)));
        Assert.Equal(RaiseEventType.HandLowered, lowered.Type);
        Assert.Equal(HandSide.Left, lowered.Hand);
    }

    [Fact]
    public void CooldownDelaysEventWithoutDroppingIt()
    {
        var tracker = new RaiseHandTracker(TestData.Options(
            consecutiveFrames: 1,
            lowerConsecutiveFrames: 1,
            cooldown: TimeSpan.FromMilliseconds(100)));

        Assert.Single(tracker.Update(1, Up, At(0)));
        Assert.Empty(tracker.Update(1, Down, At(10)));
        Assert.Empty(tracker.Update(1, Down, At(99)));
        var lowered = Assert.Single(tracker.Update(1, Down, At(100)));
        Assert.Equal(RaiseEventType.HandLowered, lowered.Type);
    }

    [Fact]
    public void TracksAreIndependent()
    {
        var tracker = new RaiseHandTracker(TestData.Options(consecutiveFrames: 2));

        Assert.Empty(tracker.Update(1, Up, At(0)));
        Assert.Empty(tracker.Update(2, Up, At(0)));
        Assert.Equal(1, Assert.Single(tracker.Update(1, Up, At(10))).TrackId);
        Assert.Equal(2, Assert.Single(tracker.Update(2, Up, At(20))).TrackId);
    }

    [Fact]
    public void PruneRemovesTrackProgress()
    {
        var tracker = new RaiseHandTracker(TestData.Options(consecutiveFrames: 2));
        tracker.Update(1, Up, At(0));

        tracker.Prune([]);

        Assert.Empty(tracker.Update(1, Up, At(10)));
        Assert.Single(tracker.Update(1, Up, At(20)));
    }

    [Fact]
    public void HoldTimeCanReplaceFrameThreshold()
    {
        var tracker = new RaiseHandTracker(TestData.Options(
            consecutiveFrames: 99,
            holdTime: TimeSpan.FromMilliseconds(100)));

        Assert.Empty(tracker.Update(1, Up, At(0)));
        Assert.Empty(tracker.Update(1, Up, At(99)));
        Assert.Single(tracker.Update(1, Up, At(100)));
    }

    private static DateTimeOffset At(int milliseconds) =>
        Start.AddMilliseconds(milliseconds);
}

