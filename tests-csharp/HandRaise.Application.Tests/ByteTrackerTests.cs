using HandRaise.Application.Inference;
using HandRaise.Application.Tracking;
using HandRaise.Domain.Detection;

namespace HandRaise.Application.Tests;

public sealed class ByteTrackerTests
{
    [Fact]
    public void ShortOcclusion_KeepsTrackId()
    {
        var tracker = new ByteTracker(Options(buffer: 2));
        var first = Assert.Single(tracker.Update([Person(0, 0.9f)], 0).People);

        tracker.Update([], 33);
        var recovered = Assert.Single(tracker.Update([Person(2, 0.9f)], 66).People);

        Assert.Equal(first.TrackId, recovered.TrackId);
    }

    [Fact]
    public void LowConfidenceSecondStage_KeepsTrackId()
    {
        var tracker = new ByteTracker(Options());
        var first = Assert.Single(tracker.Update([Person(0, 0.9f)], 0).People);

        var second = Assert.Single(tracker.Update([Person(1, 0.3f)], 33).People);

        Assert.Equal(first.TrackId, second.TrackId);
    }

    [Fact]
    public void TrackBeyondBuffer_IsRemovedAndGetsNewId()
    {
        var tracker = new ByteTracker(Options(buffer: 1));
        var first = Assert.Single(tracker.Update([Person(0, 0.9f)], 0).People);
        tracker.Update([], 33);
        tracker.Update([], 66);

        var next = Assert.Single(tracker.Update([Person(0, 0.9f)], 99).People);

        Assert.NotEqual(first.TrackId, next.TrackId);
    }

    [Fact]
    public void TwoCameraTrackers_HaveIndependentNamespaces()
    {
        var firstCamera = new ByteTracker(Options());
        var secondCamera = new ByteTracker(Options());

        Assert.Equal(1, Assert.Single(firstCamera.Update([Person(0, 0.9f)], 0).People).TrackId);
        Assert.Equal(1, Assert.Single(secondCamera.Update([Person(500, 0.9f)], 0).People).TrackId);
    }

    internal static TrackerOptions Options(int buffer = 3) => new()
    {
        HighConfidenceThreshold = 0.5f,
        LowConfidenceThreshold = 0.1f,
        NewTrackThreshold = 0.5f,
        FirstMatchIouThreshold = 0.3f,
        SecondMatchIouThreshold = 0.2f,
        LostTrackBufferFrames = buffer,
        NominalFramesPerSecond = 30,
        PositionProcessNoise = 1,
        VelocityProcessNoise = 0.1,
        MeasurementNoise = 10
    };

    internal static PosePerson Person(float offset, float confidence, bool raised = false)
    {
        var points = Enumerable.Repeat(new Keypoint(offset + 50, 100, 0.9), 17).ToArray();
        points[PoseKeypoints.LeftShoulder] = new Keypoint(offset + 30, 80, 0.9);
        points[PoseKeypoints.RightShoulder] = new Keypoint(offset + 70, 80, 0.9);
        points[PoseKeypoints.LeftWrist] = new Keypoint(offset + 30, raised ? 40 : 110, 0.9);
        points[PoseKeypoints.RightWrist] = new Keypoint(offset + 70, 110, 0.9);
        return new PosePerson(
            new BoundingBox(offset, 0, offset + 100, 160), confidence, 0, points);
    }
}
