namespace HandRaise.Application.Overlay;

public sealed record OverlayStatistics(
    double FramesPerSecond,
    double TotalLatencyMilliseconds,
    string Device,
    string Provider,
    string Model);
