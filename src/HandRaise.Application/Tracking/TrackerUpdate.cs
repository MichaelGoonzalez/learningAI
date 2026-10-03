namespace HandRaise.Application.Tracking;

public sealed record TrackerUpdate(
    IReadOnlyList<TrackedPose> People,
    IReadOnlySet<int> ActiveTrackIds);
