using HandRaise.Application.Inference;

namespace HandRaise.Application.Tracking;

public interface ITracker
{
    TrackerUpdate Update(IReadOnlyList<PosePerson> detections, double timestampMilliseconds);

    void Reset();
}
