namespace HandRaise.Application.Hardware;

public sealed record HardwareInventory(
    IReadOnlyList<DeviceInfo> Devices,
    RuntimeInfo Runtime,
    IReadOnlyList<string> Warnings);
