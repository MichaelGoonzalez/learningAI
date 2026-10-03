using HandRaise.Application.Inference;

namespace HandRaise.Application.Capture;

public sealed class VideoFrame : IDisposable
{
    private byte[]? _pixels;
    private readonly Action? _onDispose;

    public VideoFrame(
        int width,
        int height,
        byte[] bgrPixels,
        long sequence,
        double timestampMilliseconds,
        long acquiredAtStopwatchTicks,
        DateTimeOffset timestampUtc,
        Action? onDispose = null,
        double decodeMilliseconds = 0)
    {
        if (width <= 0 || height <= 0 || bgrPixels.Length != checked(width * height * 3))
        {
            throw new ArgumentException("Dimensiones o buffer BGR inválidos.");
        }

        Width = width;
        Height = height;
        _pixels = bgrPixels;
        Sequence = sequence;
        TimestampMilliseconds = timestampMilliseconds;
        AcquiredAtStopwatchTicks = acquiredAtStopwatchTicks;
        TimestampUtc = timestampUtc.ToUniversalTime();
        DecodeMilliseconds = decodeMilliseconds;
        _onDispose = onDispose;
    }

    public int Width { get; }

    public int Height { get; }

    public long Sequence { get; }

    public double TimestampMilliseconds { get; }

    public long AcquiredAtStopwatchTicks { get; }

    public DateTimeOffset TimestampUtc { get; }

    public double DecodeMilliseconds { get; }

    public ReadOnlyMemory<byte> Pixels => _pixels ?? throw new ObjectDisposedException(nameof(VideoFrame));

    public ImageFrame AsImageFrame() => new(Width, Height, Pixels);

    public VideoFrame Clone() => new(
        Width,
        Height,
        Pixels.ToArray(),
        Sequence,
        TimestampMilliseconds,
        AcquiredAtStopwatchTicks,
        TimestampUtc,
        decodeMilliseconds: DecodeMilliseconds);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _pixels, null) is not null)
        {
            _onDispose?.Invoke();
        }
    }
}
