namespace HandRaise.Infrastructure.Windows.Capture;

internal sealed record CapturedPixels(
    int Width,
    int Height,
    byte[] BgrPixels,
    double DecodeMilliseconds = 0);
