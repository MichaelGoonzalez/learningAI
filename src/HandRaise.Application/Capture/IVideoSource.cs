namespace HandRaise.Application.Capture;

public interface IVideoSource : IAsyncDisposable
{
    VideoSourceKind Kind { get; }

    string DisplayName { get; }

    double FramesPerSecond { get; }

    long DroppedFrames { get; }

    IAsyncEnumerable<VideoFrame> ReadAllAsync(CancellationToken cancellationToken = default);
}
