using System.Diagnostics;
using HandRaise.Application.Capture;
using HandRaise.Infrastructure.Windows.Capture;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class CaptureTests
{
    [Fact]
    public async Task LatestFrameChannel_DropsAndDisposesOldestFrame()
    {
        var disposed = 0;
        using var channel = new LatestFrameChannel();
        channel.Write(Frame(1, () => disposed++));
        channel.Write(Frame(2));

        using var latest = await channel.ReadAsync(CancellationToken.None);

        Assert.NotNull(latest);
        Assert.Equal(2, latest.Sequence);
        Assert.Equal(1, channel.DroppedFrames);
        Assert.Equal(1, disposed);
    }

    [Fact]
    public async Task FileVideoSource_StopsAtEndOfFileWithoutDroppingFrames()
    {
        var session = new FakeCaptureSession(opened: true, frameCount: 3, fps: 10000);
        await using var source = new FileVideoSource("video.mp4", Options(), () => session);
        var sequences = new List<long>();

        await foreach (var frame in source.ReadAllAsync())
        {
            using (frame)
            {
                sequences.Add(frame.Sequence);
            }
        }

        Assert.Equal([0, 1, 2], sequences);
        Assert.Equal(0, source.DroppedFrames);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task FileVideoSource_MapsMonotonicFramesToOrderedCurrentUtcDates()
    {
        var anchor = new DateTimeOffset(DateTimeOffset.UtcNow.Year, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var session = new FakeCaptureSession(opened: true, frameCount: 3, fps: 10000);
        await using var source = new FileVideoSource(
            "video.mp4", Options(), () => session, new FixedClock(anchor));
        var timestamps = new List<DateTimeOffset>();

        await foreach (var frame in source.ReadAllAsync())
        {
            using (frame)
            {
                timestamps.Add(frame.TimestampUtc);
            }
        }

        Assert.All(timestamps, item => Assert.Equal(DateTimeOffset.UtcNow.Year, item.Year));
        Assert.Equal(timestamps.Order(), timestamps);
        Assert.Equal(anchor, timestamps[0]);
    }

    [Fact]
    public async Task WebcamVideoSource_ReconnectsAfterOpenFailures()
    {
        var attempts = 0;
        ICaptureSession Factory()
        {
            attempts++;
            return attempts < 3
                ? new FakeCaptureSession(opened: false, frameCount: 0)
                : new FakeCaptureSession(opened: true, frameCount: 1);
        }

        await using var source = new WebcamVideoSource(0, Options(), Factory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await foreach (var frame in source.ReadAllAsync(timeout.Token))
        {
            frame.Dispose();
            break;
        }

        Assert.True(attempts >= 3);
    }

    [Fact]
    public async Task LiveSource_CancellationCompletesAndDisposesSession()
    {
        var session = new FakeCaptureSession(opened: true, frameCount: int.MaxValue);
        await using var source = new WebcamVideoSource(0, Options(), () => session);
        using var cancellation = new CancellationTokenSource();

        await foreach (var frame in source.ReadAllAsync(cancellation.Token))
        {
            frame.Dispose();
            cancellation.Cancel();
            break;
        }

        await source.DisposeAsync();
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task RtspLogs_NeverContainCredentials()
    {
        const string url = "rtsp://secret-user:secret-pass@camera.local/live";
        var logs = new List<string>();
        await using var source = new RtspVideoSource(
            url,
            Options(),
            () => new FakeCaptureSession(opened: false, frameCount: 0),
            logs.Add);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(1000));

        try
        {
            await foreach (var frame in source.ReadAllAsync(cancellation.Token))
            {
                frame.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }

        Assert.NotEmpty(logs);
        Assert.DoesNotContain(logs, message => message.Contains("secret-user"));
        Assert.DoesNotContain(logs, message => message.Contains("secret-pass"));
        Assert.Equal("rtsp://camera.local/live", source.DisplayName.TrimEnd('/'));
    }

    private static CaptureOptions Options() => new(
        Loop: false,
        FallbackFramesPerSecond: 10000,
        ReconnectInitialDelay: TimeSpan.FromMilliseconds(1),
        ReconnectMaximumDelay: TimeSpan.FromMilliseconds(4),
        OpenTimeoutMilliseconds: 10,
        ReadTimeoutMilliseconds: 10);

    private static VideoFrame Frame(long sequence, Action? onDispose = null) => new(
        1, 1, [0, 0, 0], sequence, sequence, Stopwatch.GetTimestamp(),
        DateTimeOffset.UnixEpoch.AddMilliseconds(sequence), onDispose);

    private sealed class FixedClock(DateTimeOffset now) : IUtcClock
    {
        public DateTimeOffset UtcNow => now;
    }

    private sealed class FakeCaptureSession(bool opened, int frameCount, double fps = 30) : ICaptureSession
    {
        private int _remaining = frameCount;

        public bool IsOpened { get; } = opened;

        public double FramesPerSecond { get; } = fps;

        public double PositionMilliseconds => 0;

        public bool Disposed { get; private set; }

        public bool TryRead(out CapturedPixels? pixels)
        {
            if (_remaining <= 0)
            {
                pixels = null;
                return false;
            }

            _remaining--;
            pixels = new CapturedPixels(1, 1, [0, 0, 0]);
            return true;
        }

        public bool Restart() => false;

        public void Dispose() => Disposed = true;
    }
}
