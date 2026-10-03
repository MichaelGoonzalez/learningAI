namespace HandRaise.Application.Capture;

public interface IVideoFrameSink : IAsyncDisposable
{
    ValueTask WriteAsync(VideoFrame frame, CancellationToken cancellationToken = default);
}
