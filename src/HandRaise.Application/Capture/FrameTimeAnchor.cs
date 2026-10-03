namespace HandRaise.Application.Capture;

public readonly record struct FrameTimeAnchor(
    DateTimeOffset UtcTimestamp,
    double MonotonicTimestampMilliseconds)
{
    public DateTimeOffset ToUtc(double frameTimestampMilliseconds) =>
        UtcTimestamp.ToUniversalTime().AddMilliseconds(
            frameTimestampMilliseconds - MonotonicTimestampMilliseconds);
}
