using System.Threading.Channels;
using HandRaise.Application.Capture;

namespace HandRaise.Infrastructure.Windows.Capture;

internal sealed class LatestFrameChannel : IDisposable
{
    private readonly object _sync = new();
    private readonly Channel<VideoFrame> _channel = Channel.CreateBounded<VideoFrame>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
    private long _dropped;

    public long DroppedFrames => Interlocked.Read(ref _dropped);

    public void Write(VideoFrame frame)
    {
        lock (_sync)
        {
            if (_channel.Reader.TryRead(out var previous))
            {
                previous.Dispose();
                Interlocked.Increment(ref _dropped);
            }

            if (!_channel.Writer.TryWrite(frame))
            {
                frame.Dispose();
                throw new InvalidOperationException("No fue posible publicar el frame más reciente.");
            }
        }
    }

    public async ValueTask<VideoFrame?> ReadAsync(CancellationToken cancellationToken)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken))
        {
            lock (_sync)
            {
                if (_channel.Reader.TryRead(out var frame))
                {
                    return frame;
                }
            }
        }

        return null;
    }

    public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);

    public void Dispose()
    {
        Complete();
        lock (_sync)
        {
            while (_channel.Reader.TryRead(out var frame))
            {
                frame.Dispose();
            }
        }
    }
}
