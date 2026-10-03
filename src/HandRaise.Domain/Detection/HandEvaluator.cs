namespace HandRaise.Domain.Detection;

public static class HandEvaluator
{
    public static HandsState Evaluate(
        IReadOnlyList<Keypoint> keypoints,
        DetectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(keypoints);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (keypoints.Count != PoseKeypoints.Count)
        {
            throw new ArgumentException(
                $"Se esperaban {PoseKeypoints.Count} keypoints y se recibieron {keypoints.Count}.",
                nameof(keypoints));
        }

        var margin = CalculateShoulderMargin(keypoints, options);
        var strictReference = options.StrictMode
            ? FindStrictReference(keypoints, options.KeypointConfidence)
            : null;

        var left = EvaluateSide(
            keypoints,
            PoseKeypoints.LeftWrist,
            PoseKeypoints.LeftShoulder,
            margin,
            strictReference,
            options);
        var right = EvaluateSide(
            keypoints,
            PoseKeypoints.RightWrist,
            PoseKeypoints.RightShoulder,
            margin,
            strictReference,
            options);

        var confidence = (left.Raised, right.Raised) switch
        {
            (true, true) => Math.Min(left.Confidence, right.Confidence),
            (true, false) => left.Confidence,
            (false, true) => right.Confidence,
            _ => 0,
        };
        return new HandsState(left.Raised, right.Raised, confidence);
    }

    private static double CalculateShoulderMargin(
        IReadOnlyList<Keypoint> points,
        DetectionOptions options)
    {
        if (options.ShoulderMarginRatio is { } ratio)
        {
            var left = points[PoseKeypoints.LeftShoulder];
            var right = points[PoseKeypoints.RightShoulder];
            return Math.Sqrt(
                Math.Pow(left.X - right.X, 2) +
                Math.Pow(left.Y - right.Y, 2)) * ratio;
        }

        return options.ShoulderMarginPixels!.Value;
    }

    private static StrictReference? FindStrictReference(
        IReadOnlyList<Keypoint> points,
        double confidenceThreshold)
    {
        var candidates = new[]
        {
            points[PoseKeypoints.LeftEye],
            points[PoseKeypoints.RightEye],
            points[PoseKeypoints.Nose],
        }.Where(point => point.Confidence >= confidenceThreshold).ToArray();

        return candidates.Length == 0
            ? null
            : new StrictReference(
                candidates.Min(point => point.Y),
                candidates.Min(point => point.Confidence));
    }

    private static SideResult EvaluateSide(
        IReadOnlyList<Keypoint> points,
        int wristIndex,
        int shoulderIndex,
        double shoulderMargin,
        StrictReference? strictReference,
        DetectionOptions options)
    {
        var wrist = points[wristIndex];
        var shoulder = points[shoulderIndex];
        if (wrist.Confidence < options.KeypointConfidence ||
            shoulder.Confidence < options.KeypointConfidence)
        {
            return new SideResult(false, 0);
        }

        var confidence = Math.Min(wrist.Confidence, shoulder.Confidence);
        if (options.StrictMode)
        {
            if (strictReference is not { } reference)
            {
                return new SideResult(false, 0);
            }

            var raised = wrist.Y < reference.Y - options.StrictMarginPixels;
            return raised
                ? new SideResult(true, Math.Min(confidence, reference.Confidence))
                : new SideResult(false, 0);
        }

        var isRaised = wrist.Y < shoulder.Y - shoulderMargin;
        return isRaised
            ? new SideResult(true, confidence)
            : new SideResult(false, 0);
    }

    private readonly record struct SideResult(bool Raised, double Confidence);
    private readonly record struct StrictReference(double Y, double Confidence);
}
