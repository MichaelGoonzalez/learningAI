namespace HandRaise.Application.Hardware;

public sealed record DeviceSelection(
    DeviceInfo Device,
    bool UsedFallback,
    string? Warning);
