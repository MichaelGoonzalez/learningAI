using System.Diagnostics;
using System.Runtime.CompilerServices;
using HandRaise.Application.Capture;

namespace HandRaise.Infrastructure.Windows.Capture;

public abstract class LiveVideoSourceBase : IVideoSource, IConnectionGenerationSource
{
    private readonly CaptureOptions _options;
    private readonly Func<ICaptureSession> _sessionFactory;
    private readonly Action<string>? _log;
    private readonly IUtcClock _utcClock;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _producer;
    private LatestFrameChannel? _frames;
    private bool _disposed;

    internal LiveVideoSourceBase(
        VideoSourceKind kind,
        string displayName,
        CaptureOptions options,
        Func<ICaptureSession> sessionFactory,
        Action<string>? log,
        IUtcClock? utcClock)
    {
        options.Validate();
        Kind = kind;
        DisplayName = displayName;
        _options = options;
        _sessionFactory = sessionFactory;
        _log = log;
        _utcClock = utcClock ?? SystemUtcClock.Instance;
    }

    public VideoSourceKind Kind { get; }

    public string DisplayName { get; }

    public double FramesPerSecond { get; private set; }

    public long DroppedFrames => _frames?.DroppedFrames ?? 0;

    public long ConnectionGeneration { get; private set; }

    public async IAsyncEnumerable<VideoFrame> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_producer is not null)
        {
            throw new InvalidOperationException("Una fuente viva solo admite un consumidor.");
        }

        _frames = new LatestFrameChannel();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetime.Token);
        _producer = Task.Run(() => ProduceAsync(_frames, linked.Token), CancellationToken.None);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var frame = await _frames.ReadAsync(linked.Token);
                if (frame is null)
                {
                    yield break;
                }

                yield return frame;
            }
        }
        finally
        {
            linked.Cancel();
            try
            {
                await _producer;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        if (_producer is not null)
        {
            try
            {
                await _producer.WaitAsync(TimeSpan.FromMilliseconds(_options.ReadTimeoutMilliseconds + 1000));
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
                _log?.Invoke($"La lectura de {DisplayName} no terminó dentro del timeout configurado.");
            }
        }

        _frames?.Dispose();
        _lifetime.Dispose();
    }

    private async Task ProduceAsync(LatestFrameChannel frames, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var reconnectDelay = _options.ReconnectInitialDelay;
        FrameTimeAnchor? timeAnchor = null;
        long sequence = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var session = _sessionFactory();
                if (!session.IsOpened)
                {
                    _log?.Invoke($"No fue posible abrir {DisplayName}; se reintentará.");
                    await Task.Delay(reconnectDelay, cancellationToken);
                    reconnectDelay = NextDelay(reconnectDelay);
                    continue;
                }

                FramesPerSecond = session.FramesPerSecond > 0
                    ? session.FramesPerSecond
                    : _options.FallbackFramesPerSecond;
                ConnectionGeneration++;
                timeAnchor ??= new FrameTimeAnchor(_utcClock.UtcNow, clock.Elapsed.TotalMilliseconds);
                reconnectDelay = _options.ReconnectInitialDelay;
                _log?.Invoke($"Fuente conectada: {DisplayName}.");

                while (!cancellationToken.IsCancellationRequested && session.TryRead(out var pixels))
                {
                    if (pixels is null)
                    {
                        continue;
                    }

                    var frameTimestamp = clock.Elapsed.TotalMilliseconds;
                    frames.Write(new VideoFrame(
                        pixels.Width,
                        pixels.Height,
                        pixels.BgrPixels,
                        sequence++,
                        frameTimestamp,
                        Stopwatch.GetTimestamp(),
                        timeAnchor.Value.ToUtc(frameTimestamp),
                        decodeMilliseconds: pixels.DecodeMilliseconds));
                }

                _log?.Invoke($"Se perdió {DisplayName}; iniciando reconexión.");
                await Task.Delay(reconnectDelay, cancellationToken);
                reconnectDelay = NextDelay(reconnectDelay);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            frames.Complete(exception);
            return;
        }

        frames.Complete();
    }

    private TimeSpan NextDelay(TimeSpan current) => TimeSpan.FromMilliseconds(
        Math.Min(current.TotalMilliseconds * 2, _options.ReconnectMaximumDelay.TotalMilliseconds));
}
