namespace HandRaise.Application.Storage;

public sealed record EventStatistic(
    string CameraId,
    string? Zone,
    DateTimeOffset HourUtc,
    long Count);
