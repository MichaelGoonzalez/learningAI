namespace HandRaise.Infrastructure.Windows.Hardware;

internal sealed record NvidiaGpu(
    int Index,
    string Uuid,
    string Name,
    long? VramMb,
    string? DriverVersion);
