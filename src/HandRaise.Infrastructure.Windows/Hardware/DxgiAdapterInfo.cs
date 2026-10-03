using HandRaise.Application.Hardware;

namespace HandRaise.Infrastructure.Windows.Hardware;

internal sealed record DxgiAdapterInfo(
    int Index,
    string StableId,
    string Name,
    HardwareVendor Vendor,
    long? VramMb);
