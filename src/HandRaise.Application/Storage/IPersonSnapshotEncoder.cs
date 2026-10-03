using HandRaise.Application.Capture;
using HandRaise.Application.Inference;

namespace HandRaise.Application.Storage;

public interface IPersonSnapshotEncoder
{
    byte[] Encode(VideoFrame frame, BoundingBox box, SnapshotEncodingOptions options);
}
