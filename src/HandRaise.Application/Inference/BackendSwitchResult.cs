using HandRaise.Application.Hardware;

namespace HandRaise.Application.Inference;

public sealed record BackendSwitchResult(
    bool Success,
    DeviceInfo ActiveDevice,
    TimeSpan Duration,
    string? Error);
