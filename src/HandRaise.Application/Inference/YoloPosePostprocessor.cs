using HandRaise.Domain.Detection;

namespace HandRaise.Application.Inference;

public static class YoloPosePostprocessor
{
    public static IReadOnlyList<PosePerson> Process(
        ReadOnlySpan<float> output,
        IReadOnlyList<long> shape,
        LetterboxTransform transform,
        ModelDescriptor model)
    {
        model.Validate();
        if (shape.Count != 3 || shape[0] != 1)
        {
            throw new InvalidDataException(
                $"Se esperaba una salida de tres dimensiones con batch 1; se recibió [{string.Join(',', shape)}].");
        }

        var expectedValues = checked((int)(shape[0] * shape[1] * shape[2]));
        if (output.Length != expectedValues)
        {
            throw new InvalidDataException(
                $"La salida contiene {output.Length} valores pero su forma declara {expectedValues}.");
        }

        var endToEndAttributes = 6 + model.KeypointCount * 3;
        var legacyAttributes = 4 + model.ClassCount + model.KeypointCount * 3;

        if (shape[2] == endToEndAttributes)
        {
            return ProcessEndToEnd(output, checked((int)shape[1]), transform, model);
        }

        if (shape[1] == legacyAttributes)
        {
            return ProcessLegacy(output, checked((int)shape[2]), transform, model, channelFirst: true);
        }

        if (shape[2] == legacyAttributes)
        {
            return ProcessLegacy(output, checked((int)shape[1]), transform, model, channelFirst: false);
        }

        throw new InvalidDataException(
            $"La forma [{string.Join(',', shape)}] no coincide con YOLO Pose " +
            $"({model.ClassCount} clases, {model.KeypointCount} keypoints)." );
    }

    private static IReadOnlyList<PosePerson> ProcessEndToEnd(
        ReadOnlySpan<float> output,
        int candidates,
        LetterboxTransform transform,
        ModelDescriptor model)
    {
        var attributes = 6 + model.KeypointCount * 3;
        var people = new List<PosePerson>();
        for (var candidate = 0; candidate < candidates && people.Count < model.MaximumDetections; candidate++)
        {
            var offset = candidate * attributes;
            var confidence = output[offset + 4];
            var classId = (int)output[offset + 5];
            if (confidence < model.ConfidenceThreshold || classId < 0 || classId >= model.ClassCount)
            {
                continue;
            }

            people.Add(CreatePerson(
                output[offset], output[offset + 1], output[offset + 2], output[offset + 3],
                confidence, classId, output.Slice(offset + 6, model.KeypointCount * 3), transform));
        }

        return people;
    }

    private static IReadOnlyList<PosePerson> ProcessLegacy(
        ReadOnlySpan<float> output,
        int candidates,
        LetterboxTransform transform,
        ModelDescriptor model,
        bool channelFirst)
    {
        var attributes = 4 + model.ClassCount + model.KeypointCount * 3;

        var detections = new List<PosePerson>();
        for (var candidate = 0; candidate < candidates; candidate++)
        {
            var classId = 0;
            var confidence = ReadLegacy(output, candidate, 4, candidates, attributes, channelFirst);
            for (var classIndex = 1; classIndex < model.ClassCount; classIndex++)
            {
                var score = ReadLegacy(
                    output, candidate, 4 + classIndex, candidates, attributes, channelFirst);
                if (score > confidence)
                {
                    confidence = score;
                    classId = classIndex;
                }
            }

            if (confidence < model.ConfidenceThreshold)
            {
                continue;
            }

            var centerX = ReadLegacy(output, candidate, 0, candidates, attributes, channelFirst);
            var centerY = ReadLegacy(output, candidate, 1, candidates, attributes, channelFirst);
            var width = ReadLegacy(output, candidate, 2, candidates, attributes, channelFirst);
            var height = ReadLegacy(output, candidate, 3, candidates, attributes, channelFirst);
            var keypoints = new float[model.KeypointCount * 3];
            for (var index = 0; index < keypoints.Length; index++)
            {
                keypoints[index] = ReadLegacy(
                    output,
                    candidate,
                    4 + model.ClassCount + index,
                    candidates,
                    attributes,
                    channelFirst);
            }

            detections.Add(CreatePerson(
                centerX - width / 2,
                centerY - height / 2,
                centerX + width / 2,
                centerY + height / 2,
                confidence,
                classId,
                keypoints,
                transform));
        }

        return ApplyNonMaximumSuppression(detections, model.IouThreshold, model.MaximumDetections);
    }

    private static float ReadLegacy(
        ReadOnlySpan<float> output,
        int candidate,
        int attribute,
        int candidates,
        int attributes,
        bool channelFirst) => channelFirst
        ? output[attribute * candidates + candidate]
        : output[candidate * attributes + attribute];

    private static PosePerson CreatePerson(
        float left,
        float top,
        float right,
        float bottom,
        float confidence,
        int classId,
        ReadOnlySpan<float> rawKeypoints,
        LetterboxTransform transform)
    {
        var keypoints = new Keypoint[rawKeypoints.Length / 3];
        for (var index = 0; index < keypoints.Length; index++)
        {
            keypoints[index] = new Keypoint(
                transform.ToSourceX(rawKeypoints[index * 3]),
                transform.ToSourceY(rawKeypoints[index * 3 + 1]),
                rawKeypoints[index * 3 + 2]);
        }

        return new PosePerson(
            new BoundingBox(
                transform.ToSourceX(left),
                transform.ToSourceY(top),
                transform.ToSourceX(right),
                transform.ToSourceY(bottom)),
            confidence,
            classId,
            keypoints);
    }

    private static IReadOnlyList<PosePerson> ApplyNonMaximumSuppression(
        IEnumerable<PosePerson> detections,
        float iouThreshold,
        int maximumDetections)
    {
        var pending = detections.OrderByDescending(detection => detection.Confidence).ToList();
        var selected = new List<PosePerson>();
        while (pending.Count > 0 && selected.Count < maximumDetections)
        {
            var current = pending[0];
            pending.RemoveAt(0);
            selected.Add(current);
            pending.RemoveAll(candidate =>
                candidate.ClassId == current.ClassId && IoU(current.Box, candidate.Box) > iouThreshold);
        }

        return selected;
    }

    private static float IoU(BoundingBox left, BoundingBox right)
    {
        var intersectionLeft = Math.Max(left.Left, right.Left);
        var intersectionTop = Math.Max(left.Top, right.Top);
        var intersectionRight = Math.Min(left.Right, right.Right);
        var intersectionBottom = Math.Min(left.Bottom, right.Bottom);
        var intersection = Math.Max(0, intersectionRight - intersectionLeft) *
            Math.Max(0, intersectionBottom - intersectionTop);
        var union = left.Area + right.Area - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}
