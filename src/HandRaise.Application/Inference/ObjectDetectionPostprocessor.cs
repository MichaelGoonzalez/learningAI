namespace HandRaise.Application.Inference;

/// <summary>Fixed raw detect contract: [1,4+C,N], xywh in model pixels, class probabilities, no keypoints.</summary>
public static class ObjectDetectionPostprocessor
{
    public static IReadOnlyList<PosePerson> Process(ReadOnlySpan<float> output, long[] shape,
        LetterboxTransform transform, ModelDescriptor model)
    {
        if (model.KeypointCount != 0 || model.ClassCount < 1 || shape.Length != 3 || shape[0] != 1
            || shape[1] != 4 + model.ClassCount || shape[2] <= 6 || shape[2] > int.MaxValue
            || output.Length != checked(shape[1] * shape[2]))
            throw new ArgumentException("Formato de detección incompatible.");
        var count = (int)shape[2];
        var candidates = new List<PosePerson>();
        for (var i = 0; i < count; i++)
        {
            var confidence = 0f;
            var cls = 0;
            for (var c = 0; c < model.ClassCount; c++)
            {
                var score = output[(4 + c) * count + i];
                if (!float.IsFinite(score) || score < 0 || score > 1) throw new ArgumentException("Confianza inválida.");
                if (score > confidence) { confidence = score; cls = c; }
            }
            var x = output[i]; var y = output[count + i];
            var w = output[2 * count + i]; var h = output[3 * count + i];
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(w) || !float.IsFinite(h))
                throw new ArgumentException("Coordenadas inválidas.");
            if (confidence < model.ConfidenceThreshold || w <= 0 || h <= 0) continue;
            var box = new BoundingBox(transform.ToSourceX(x - w / 2), transform.ToSourceY(y - h / 2),
                transform.ToSourceX(x + w / 2), transform.ToSourceY(y + h / 2));
            if (box.Area > 0) candidates.Add(new(box, confidence, cls, []));
        }
        var kept = new List<PosePerson>();
        foreach (var candidate in candidates.OrderByDescending(c => c.Confidence))
        {
            if (kept.Any(k => k.ClassId == candidate.ClassId && IoU(k.Box, candidate.Box) > model.IouThreshold)) continue;
            kept.Add(candidate);
            if (kept.Count >= model.MaximumDetections) break;
        }
        return kept;
    }

    private static float IoU(BoundingBox a, BoundingBox b)
    {
        var intersection = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left))
            * Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top));
        return intersection / (a.Area + b.Area - intersection);
    }
}
