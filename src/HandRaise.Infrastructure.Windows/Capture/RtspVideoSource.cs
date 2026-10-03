using HandRaise.Application.Capture;

namespace HandRaise.Infrastructure.Windows.Capture;

public sealed class RtspVideoSource : LiveVideoSourceBase
{
    public RtspVideoSource(
        string url,
        CaptureOptions options,
        Action<string>? log = null,
        IUtcClock? utcClock = null)
        : base(
            VideoSourceKind.Rtsp,
            Sanitize(url),
            options,
            () => new OpenCvCaptureSession(url, options),
            log,
            utcClock)
    {
    }

    internal RtspVideoSource(
        string url,
        CaptureOptions options,
        Func<ICaptureSession> sessionFactory,
        Action<string>? log = null,
        IUtcClock? utcClock = null)
        : base(VideoSourceKind.Rtsp, Sanitize(url), options, sessionFactory, log, utcClock)
    {
    }

    internal static string Sanitize(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, "rtsp", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("La fuente RTSP no es una URL válida.", nameof(url));
        }

        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        return builder.Uri.AbsoluteUri;
    }
}
