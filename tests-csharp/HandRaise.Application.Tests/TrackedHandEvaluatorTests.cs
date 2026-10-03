using HandRaise.Application.Events;
using HandRaise.Application.Tracking;
using HandRaise.Domain.Detection;
using System.Text.Json;

namespace HandRaise.Application.Tests;

public sealed class TrackedHandEvaluatorTests
{
    [Fact]
    public void BlinkOfOneOrTwoFrames_EmitsNoEvents()
    {
        var session = new Session();

        Assert.Empty(session.Frame(true, 0));
        Assert.Empty(session.Frame(true, 33));
        Assert.Empty(session.Frame(false, 66));
    }

    [Fact]
    public void SustainedRaiseAndLower_EmitExactlyOneOfEach()
    {
        var session = new Session();
        var events = new List<HandEvent>();
        for (var index = 0; index < 8; index++)
        {
            events.AddRange(session.Frame(index < 5, index * 33));
        }

        Assert.Equal(["hand_raised", "hand_lowered"], events.Select(item => item.Type));
        Assert.Single(events, item => item.Type == "hand_raised");
        Assert.Single(events, item => item.Type == "hand_lowered");
    }

    [Fact]
    public void TwoPeople_HaveIndependentState()
    {
        var tracker = new ByteTracker(ByteTrackerTests.Options());
        var evaluator = new TrackedHandEvaluator(Detection());
        var events = new List<HandEvent>();
        for (var frame = 0; frame < 3; frame++)
        {
            var tracked = tracker.Update(
                [ByteTrackerTests.Person(0, 0.9f, true), ByteTrackerTests.Person(300, 0.9f, true)],
                frame * 33);
            events.AddRange(evaluator.Evaluate("camera", tracked, frame * 33, At(frame * 33), []).Events);
        }

        Assert.Equal(2, events.Count);
        Assert.Equal(2, events.Select(item => item.TrackId).Distinct().Count());
    }

    [Fact]
    public void DisappearingTrack_ProducesNoGhostEvents()
    {
        var tracker = new ByteTracker(ByteTrackerTests.Options(buffer: 1));
        var evaluator = new TrackedHandEvaluator(Detection());
        for (var frame = 0; frame < 3; frame++)
        {
            var tracked = tracker.Update([ByteTrackerTests.Person(0, 0.9f, true)], frame * 33);
            evaluator.Evaluate("camera", tracked, frame * 33, At(frame * 33), []);
        }

        var events = new List<HandEvent>();
        for (var frame = 3; frame < 8; frame++)
        {
            var tracked = tracker.Update([], frame * 33);
            events.AddRange(evaluator.Evaluate("camera", tracked, frame * 33, At(frame * 33), []).Events);
        }

        Assert.Empty(events);
    }

    [Fact]
    public void DetectionWithoutTrackId_IsDrawnButEmitsNoEvent()
    {
        var evaluator = new TrackedHandEvaluator(Detection() with { ConsecutiveFrames = 1 });
        var pose = ByteTrackerTests.Person(0, 0.9f, raised: true);
        var update = new TrackerUpdate([new TrackedPose(pose, null)], new HashSet<int>());

        var result = evaluator.Evaluate("camera", update, 0, At(0), []);

        Assert.Single(result.People);
        Assert.Null(result.People[0].TrackId);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void ReplayingSameSequence_IsDeterministic()
    {
        static string Replay()
        {
            var session = new Session();
            var events = new List<HandEvent>();
            for (var index = 0; index < 8; index++)
            {
                events.AddRange(session.Frame(index < 5, index * 33));
            }

            Assert.All(events, item => Assert.Equal(DateTimeOffset.UtcNow.Year, item.Timestamp.Year));
            Assert.Equal(events.OrderBy(item => item.Timestamp), events);
            return string.Join('\n', events.Select(item => JsonSerializer.Serialize(item)));
        }

        Assert.Equal(Replay(), Replay());
    }

    [Fact]
    public async Task EventBus_BroadcastsToIndependentSubscribers()
    {
        await using var bus = new HandEventBus();
        await using var first = bus.Subscribe();
        await using var second = bus.Subscribe();
        var handEvent = new HandEvent(
            "id", "hand_raised", "camera", 1, "left", null, 0.9,
            DateTimeOffset.UnixEpoch);

        await bus.PublishAsync(handEvent);

        Assert.Equal(handEvent, await first.Reader.ReadAsync());
        Assert.Equal(handEvent, await second.Reader.ReadAsync());
    }

    private static DetectionOptions Detection() => new()
    {
        KeypointConfidence = 0.5,
        ShoulderMarginRatio = 0.15,
        StrictMode = false,
        StrictMarginPixels = 0,
        ConsecutiveFrames = 3,
        LowerConsecutiveFrames = 2,
        Cooldown = TimeSpan.Zero,
        TrackTimeToLive = TimeSpan.FromSeconds(1)
    };

    private sealed class Session
    {
        private readonly ByteTracker _tracker = new(ByteTrackerTests.Options());
        private readonly TrackedHandEvaluator _evaluator = new(Detection());

        public IReadOnlyList<HandEvent> Frame(bool raised, double timestamp)
        {
            var tracked = _tracker.Update(
                [ByteTrackerTests.Person(0, 0.9f, raised)], timestamp);
            return _evaluator.Evaluate("camera", tracked, timestamp, At(timestamp), []).Events;
        }
    }

    private static DateTimeOffset At(double milliseconds) =>
        new DateTimeOffset(DateTimeOffset.UtcNow.Year, 1, 1, 0, 0, 0, TimeSpan.Zero)
            .AddMilliseconds(milliseconds);
}
