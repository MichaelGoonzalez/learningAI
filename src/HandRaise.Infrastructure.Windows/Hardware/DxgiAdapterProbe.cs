using HandRaise.Application.Hardware;
using System.Globalization;
using Vortice.DXGI;
using static Vortice.DXGI.DXGI;

namespace HandRaise.Infrastructure.Windows.Hardware;

internal sealed class DxgiAdapterProbe
{
    public IReadOnlyList<DxgiAdapterInfo> Detect()
    {
        var devices = new List<DxgiAdapterInfo>();
        using var factory = CreateDXGIFactory1<IDXGIFactory1>();

        for (var index = 0; factory.EnumAdapters1((uint)index, out var adapter).Success; index++)
        {
            using (adapter)
            {
                var description = adapter.Description1;
                if ((description.Flags & AdapterFlags.Software) != 0 ||
                    description.VendorId == 0x1414)
                {
                    continue;
                }

                var luid = description.Luid.ToString("X16", CultureInfo.InvariantCulture);
                var stableId = FormattableString.Invariant(
                    $"directml:{description.VendorId:X4}:{description.DeviceId:X4}:{luid}");
                long? vramMb = description.DedicatedVideoMemory > 0
                    ? checked(description.DedicatedVideoMemory / (1024L * 1024L))
                    : null;

                devices.Add(new DxgiAdapterInfo(
                    index,
                    stableId,
                    description.Description.TrimEnd('\0').Trim(),
                    MapVendor(description.VendorId),
                    vramMb));
            }
        }

        return devices;
    }

    internal static HardwareVendor MapVendor(uint vendorId) => vendorId switch
    {
        0x10DE => HardwareVendor.Nvidia,
        0x1002 or 0x1022 => HardwareVendor.Amd,
        0x8086 => HardwareVendor.Intel,
        _ => HardwareVendor.Other
    };
}
