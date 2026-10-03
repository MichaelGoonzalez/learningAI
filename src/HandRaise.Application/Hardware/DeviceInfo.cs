namespace HandRaise.Application.Hardware;

public sealed record DeviceInfo(
    string Id,
    string Name,
    HardwareVendor Vendor,
    InferenceBackend Backend,
    int? AdapterIndex,
    long? VramMb,
    string? DriverVersion,
    bool RuntimeAvailable,
    string? UnavailableReason = null);
