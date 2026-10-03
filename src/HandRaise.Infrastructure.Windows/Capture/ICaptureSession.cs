namespace HandRaise.Infrastructure.Windows.Capture;

internal interface ICaptureSession : IDisposable
{
    bool IsOpened { get; }

    double FramesPerSecond { get; }

    double PositionMilliseconds { get; }

    bool TryRead(out CapturedPixels? pixels);

    bool Restart();
}
