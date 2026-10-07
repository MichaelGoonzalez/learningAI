using System.Runtime.InteropServices;
using HandRaise.Application.Capture;
using HandRaise.Application.Overlay;
using HandRaise.Domain.Detection;
using HandRaise.Domain.Zones;
using OpenCvSharp;

namespace HandRaise.Infrastructure.Windows.Overlay;

public sealed class OpenCvOverlayRenderer : IFrameOverlay
{
    private readonly double _minimumKeypointConfidence;

    private static readonly (int Start, int End)[] Skeleton =
    [
        (0, 1), (0, 2), (1, 3), (2, 4),
        (5, 6), (5, 7), (7, 9), (6, 8), (8, 10),
        (5, 11), (6, 12), (11, 12),
        (11, 13), (13, 15), (12, 14), (14, 16)
    ];

    public OpenCvOverlayRenderer(double minimumKeypointConfidence)
    {
        if (minimumKeypointConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumKeypointConfidence));
        }

        _minimumKeypointConfidence = minimumKeypointConfidence;
    }

    public VideoFrame Draw(
        VideoFrame source,
        IReadOnlyList<EvaluatedPerson> people,
        IReadOnlyList<ZoneDefinition> zones,
        OverlayStatistics statistics)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var mat = ToMat(source);
        DrawZones(mat, zones);

        foreach (var person in people)
        {
            DrawPerson(mat, person, _minimumKeypointConfidence);
        }

        DrawStatistics(mat, statistics);
        var output = new byte[checked(source.Width * source.Height * 3)];
        Marshal.Copy(mat.Data, output, 0, output.Length);
        return new VideoFrame(
            source.Width,
            source.Height,
            output,
            source.Sequence,
            source.TimestampMilliseconds,
            source.AcquiredAtStopwatchTicks,
            source.TimestampUtc);
    }

    private static Mat ToMat(VideoFrame frame)
    {
        var mat = new Mat(frame.Height, frame.Width, MatType.CV_8UC3);
        if (MemoryMarshal.TryGetArray(frame.Pixels, out var segment) && segment.Array is not null)
        {
            Marshal.Copy(segment.Array, segment.Offset, mat.Data, segment.Count);
        }
        else
        {
            var span = frame.Pixels.Span;
            var temp = new byte[span.Length];
            span.CopyTo(temp);
            Marshal.Copy(temp, 0, mat.Data, temp.Length);
        }
        return mat;
    }

    private static void DrawZones(Mat mat, IReadOnlyList<ZoneDefinition> zones)
    {
        foreach (var zone in zones)
        {
            zone.Validate();
            var points = zone.Points
                .Select(point => new Point((int)Math.Round(point.X), (int)Math.Round(point.Y)))
                .ToArray();
            Cv2.Polylines(mat, [points], true, new Scalar(0, 215, 255), 2, LineTypes.AntiAlias);
            Cv2.PutText(
                mat,
                zone.Name,
                points[0],
                HersheyFonts.HersheySimplex,
                0.55,
                new Scalar(0, 215, 255),
                2,
                LineTypes.AntiAlias);
        }
    }

    private static void DrawPerson(Mat mat, EvaluatedPerson person, double minimumKeypointConfidence)
    {
        var detection = person.Detection;
        var box = new Rect(
            (int)Math.Round(detection.Box.Left),
            (int)Math.Round(detection.Box.Top),
            Math.Max(1, (int)Math.Round(detection.Box.Width)),
            Math.Max(1, (int)Math.Round(detection.Box.Height)));
        box = box.Intersect(new Rect(0, 0, mat.Width, mat.Height));
        var raisedColor = new Scalar(30, 30, 240);
        var normalColor = new Scalar(60, 210, 60);
        var color = person.Hands.AnyRaised ? raisedColor : normalColor;
        Cv2.Rectangle(mat, box, color, 2, LineTypes.AntiAlias);

        foreach (var (start, end) in Skeleton)
        {
            var first = detection.Keypoints[start];
            var second = detection.Keypoints[end];
            if (first.Confidence < minimumKeypointConfidence || second.Confidence < minimumKeypointConfidence)
            {
                continue;
            }

            Cv2.Line(
                mat,
                new Point((int)Math.Round(first.X), (int)Math.Round(first.Y)),
                new Point((int)Math.Round(second.X), (int)Math.Round(second.Y)),
                new Scalar(255, 180, 40),
                2,
                LineTypes.AntiAlias);
        }

        foreach (var keypoint in detection.Keypoints.Where(
            keypoint => keypoint.Confidence >= minimumKeypointConfidence))
        {
            Cv2.Circle(
                mat,
                new Point((int)Math.Round(keypoint.X), (int)Math.Round(keypoint.Y)),
                3,
                new Scalar(255, 255, 255),
                -1,
                LineTypes.AntiAlias);
        }

        var identity = person.TrackId is { } trackId ? $"ID {trackId}" : "sin ID";
        var phase = person.Phase is TrackPhase.Raising or TrackPhase.Raised
            ? $" {person.Phase.Value.ToString().ToUpperInvariant()}"
            : string.Empty;
        var score = $"{identity}{phase} persona {detection.Confidence:0.00}";
        Cv2.PutText(
            mat,
            score,
            new Point(box.X, Math.Max(18, box.Y - 7)),
            HersheyFonts.HersheySimplex,
            0.55,
            color,
            2,
            LineTypes.AntiAlias);

        if (person.Hands.Hand is { } hand)
        {
            var label = hand switch
            {
                HandSide.Left => "MANO I",
                HandSide.Right => "MANO D",
                HandSide.Both => "MANO AMBAS",
                _ => string.Empty
            };
            Cv2.PutText(
                mat,
                label,
                new Point(box.X, Math.Min(mat.Height - 8, box.Bottom + 22)),
                HersheyFonts.HersheySimplex,
                0.7,
                raisedColor,
                2,
                LineTypes.AntiAlias);
        }
    }

    private static void DrawStatistics(Mat mat, OverlayStatistics statistics)
    {
        if (string.IsNullOrWhiteSpace(statistics.Model))
        {
            return;
        }

        var lines = new[]
        {
            $"FPS {statistics.FramesPerSecond:0.0} | {statistics.TotalLatencyMilliseconds:0.0} ms",
            $"{statistics.Device} | {statistics.Provider}",
            statistics.Model
        };
        for (var index = 0; index < lines.Length; index++)
        {
            Cv2.PutText(
                mat,
                lines[index],
                new Point(10, 24 + index * 22),
                HersheyFonts.HersheySimplex,
                0.55,
                new Scalar(255, 255, 255),
                2,
                LineTypes.AntiAlias);
        }
    }
}
