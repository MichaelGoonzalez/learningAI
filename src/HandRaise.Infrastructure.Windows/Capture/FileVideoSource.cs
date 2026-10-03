using System.Diagnostics;
using System.Runtime.CompilerServices;
using HandRaise.Application.Capture;

namespace HandRaise.Infrastructure.Windows.Capture;

public sealed class FileVideoSource : IVideoSource
{
    private readonly CaptureOptions _options;
    private readonly Func<ICaptureSession> _sessionFactory;
    private readonly IUtcClock _utcClock;
    private bool _disposed;

    public FileVideoSource(string path, CaptureOptions options, IUtcClock? utcClock = null)
        : this(path, options, () => new OpenCvCaptureSession(path, options), utcClock)
    {
    }

    internal FileVideoSource(
        string path,
        CaptureOptions options,
        Func<ICaptureSession> sessionFactory,
        IUtcClock? utcClock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        options.Validate();
        DisplayName = Path.GetFileName(path);
        _options = options;
        _sessionFactory = sessionFactory;
        _utcClock = utcClock ?? SystemUtcClock.Instance;
    }

    public VideoSourceKind Kind => VideoSourceKind.File;

    public string DisplayName { get; }

    public double FramesPerSecond { get; private set; }

    public long DroppedFrames => 0;

    public async IAsyncEnumerable<VideoFrame> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var session = _sessionFactory();
        if (!session.IsOpened)
        {
            throw new IOException($"No fue posible abrir el archivo '{DisplayName}'.");
        }

        FramesPerSecond = session.FramesPerSecond > 0
            ? session.FramesPerSecond
            : _options.FallbackFramesPerSecond;
        var start = Stopwatch.GetTimestamp();
        var timeAnchor = new FrameTimeAnchor(_utcClock.UtcNow, 0);
        long sequence = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!session.TryRead(out var pixels) || pixels is null)
            {
                if (!_options.Loop || !session.Restart())
                {
                    yield break;
                }

                continue;
            }

            var target = TimeSpan.FromSeconds(sequence / FramesPerSecond);
            var elapsed = Stopwatch.GetElapsedTime(start);
            if (target > elapsed)
            {
                await Task.Delay(target - elapsed, cancellationToken);
            }

            // El índice global conserva un timestamp monotónico incluso al repetir el archivo.
            var mediaTimestamp = sequence * 1000d / FramesPerSecond;
            yield return new VideoFrame(
                pixels.Width,
                pixels.Height,
                pixels.BgrPixels,
                sequence,
                mediaTimestamp,
                Stopwatch.GetTimestamp(),
                timeAnchor.ToUtc(mediaTimestamp),
                decodeMilliseconds: pixels.DecodeMilliseconds);
            sequence++;
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
