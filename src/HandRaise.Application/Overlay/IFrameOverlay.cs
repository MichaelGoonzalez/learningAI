using HandRaise.Application.Capture;
using HandRaise.Domain.Zones;

namespace HandRaise.Application.Overlay;

public interface IFrameOverlay
{
    VideoFrame Draw(
        VideoFrame source,
        IReadOnlyList<EvaluatedPerson> people,
        IReadOnlyList<ZoneDefinition> zones,
        OverlayStatistics statistics);
}
