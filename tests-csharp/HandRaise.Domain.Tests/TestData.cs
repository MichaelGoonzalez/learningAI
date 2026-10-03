using HandRaise.Domain.Detection;

namespace HandRaise.Domain.Tests;

internal static class TestData
{
    public static DetectionOptions Options(
        bool strictMode = false,
        int consecutiveFrames = 3,
        int lowerConsecutiveFrames = 2,
        TimeSpan? holdTime = null,
        TimeSpan? cooldown = null) => new()
    {
        KeypointConfidence = 0.5,
        ShoulderMarginPixels = null,
        ShoulderMarginRatio = 0.2,
        StrictMode = strictMode,
        StrictMarginPixels = 0,
        ConsecutiveFrames = consecutiveFrames,
        HoldTime = holdTime,
        LowerConsecutiveFrames = lowerConsecutiveFrames,
        Cooldown = cooldown ?? TimeSpan.Zero,
        TrackTimeToLive = TimeSpan.FromSeconds(1),
    };

    public static Keypoint[] Pose(double scale = 1)
    {
        var points = Enumerable.Repeat(new Keypoint(0, 0, 0.9), 17).ToArray();
        points[PoseKeypoints.Nose] = Scale(new Keypoint(150, 80, 0.9), scale);
        points[PoseKeypoints.LeftEye] = Scale(new Keypoint(140, 75, 0.9), scale);
        points[PoseKeypoints.RightEye] = Scale(new Keypoint(160, 75, 0.9), scale);
        points[PoseKeypoints.LeftShoulder] = Scale(new Keypoint(100, 150, 0.9), scale);
        points[PoseKeypoints.RightShoulder] = Scale(new Keypoint(200, 150, 0.9), scale);
        points[PoseKeypoints.LeftWrist] = Scale(new Keypoint(100, 190, 0.9), scale);
        points[PoseKeypoints.RightWrist] = Scale(new Keypoint(200, 190, 0.9), scale);
        return points;
    }

    private static Keypoint Scale(Keypoint point, double scale) =>
        point with { X = point.X * scale, Y = point.Y * scale };
}

