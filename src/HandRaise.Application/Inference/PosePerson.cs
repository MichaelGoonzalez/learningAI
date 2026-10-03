using HandRaise.Domain.Detection;

namespace HandRaise.Application.Inference;

public sealed record PosePerson(
    BoundingBox Box,
    float Confidence,
    int ClassId,
    IReadOnlyList<Keypoint> Keypoints);
