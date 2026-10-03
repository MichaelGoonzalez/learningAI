using HandRaise.Application.Inference;

namespace HandRaise.Application.Tracking;

public sealed record TrackedPose(PosePerson Detection, int? TrackId);
