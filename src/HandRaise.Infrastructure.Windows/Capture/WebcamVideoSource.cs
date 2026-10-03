using HandRaise.Application.Capture;

namespace HandRaise.Infrastructure.Windows.Capture;

public sealed class WebcamVideoSource : LiveVideoSourceBase
{
    public WebcamVideoSource(
        int index,
        CaptureOptions options,
        Action<string>? log = null,
        IUtcClock? utcClock = null)
        : base(
            VideoSourceKind.Webcam,
            $"webcam:{index}",
            options,
            () => new OpenCvCaptureSession(index, options),
            log,
            utcClock)
    {
    }

    internal WebcamVideoSource(
        int index,
        CaptureOptions options,
        Func<ICaptureSession> sessionFactory,
        Action<string>? log = null,
        IUtcClock? utcClock = null)
        : base(VideoSourceKind.Webcam, $"webcam:{index}", options, sessionFactory, log, utcClock)
    {
    }
}
