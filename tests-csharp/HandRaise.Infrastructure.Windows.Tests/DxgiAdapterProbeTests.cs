using HandRaise.Application.Hardware;
using HandRaise.Infrastructure.Windows.Hardware;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class DxgiAdapterProbeTests
{
    [Theory]
    [InlineData(0x10DEu, HardwareVendor.Nvidia)]
    [InlineData(0x1002u, HardwareVendor.Amd)]
    [InlineData(0x1022u, HardwareVendor.Amd)]
    [InlineData(0x8086u, HardwareVendor.Intel)]
    [InlineData(0x1234u, HardwareVendor.Other)]
    public void MapVendor_MapsKnownPciVendorIds(uint id, HardwareVendor expected)
    {
        Assert.Equal(expected, DxgiAdapterProbe.MapVendor(id));
    }
}
