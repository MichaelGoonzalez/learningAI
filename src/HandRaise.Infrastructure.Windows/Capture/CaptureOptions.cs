namespace HandRaise.Infrastructure.Windows.Capture;

public sealed record CaptureOptions(
    bool Loop,
    double FallbackFramesPerSecond,
    TimeSpan ReconnectInitialDelay,
    TimeSpan ReconnectMaximumDelay,
    int OpenTimeoutMilliseconds,
    int ReadTimeoutMilliseconds)
{
    public void Validate()
    {
        if (FallbackFramesPerSecond <= 0 ||
            ReconnectInitialDelay <= TimeSpan.Zero ||
            ReconnectMaximumDelay < ReconnectInitialDelay ||
            OpenTimeoutMilliseconds <= 0 ||
            ReadTimeoutMilliseconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(CaptureOptions));
        }
    }
}
