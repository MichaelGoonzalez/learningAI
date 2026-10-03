using HandRaise.Application.Hardware;

namespace HandRaise.Infrastructure.Windows.Hardware;

public sealed class WindowsDeviceDetector : IDeviceDetector
{
    private readonly RuntimeProbe _runtimeProbe;
    private readonly NvidiaSmiProbe _nvidiaProbe;
    private readonly DxgiAdapterProbe _dxgiProbe;

    public WindowsDeviceDetector(TimeSpan commandTimeout)
        : this(
            new RuntimeProbe(),
            new NvidiaSmiProbe(commandTimeout),
            new DxgiAdapterProbe())
    {
    }

    internal WindowsDeviceDetector(
        RuntimeProbe runtimeProbe,
        NvidiaSmiProbe nvidiaProbe,
        DxgiAdapterProbe dxgiProbe)
    {
        _runtimeProbe = runtimeProbe;
        _nvidiaProbe = nvidiaProbe;
        _dxgiProbe = dxgiProbe;
    }

    public async Task<HardwareInventory> DetectAsync(CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();
        var runtime = _runtimeProbe.Detect();
        var devices = new List<DeviceInfo>
        {
            new(
                "cpu",
                "CPU",
                HardwareVendor.Cpu,
                InferenceBackend.Cpu,
                AdapterIndex: null,
                VramMb: null,
                DriverVersion: null,
                RuntimeAvailable: runtime.CpuAvailable)
        };

        var (nvidiaDevices, nvidiaWarning) = await _nvidiaProbe.DetectAsync(cancellationToken);
        if (nvidiaWarning is not null)
        {
            warnings.Add(nvidiaWarning);
        }

        try
        {
            foreach (var adapter in _dxgiProbe.Detect())
            {
                var nvidiaMatch = nvidiaDevices.FirstOrDefault(gpu =>
                    string.Equals(gpu.Name, adapter.Name, StringComparison.OrdinalIgnoreCase));
                devices.Add(new DeviceInfo(
                    adapter.StableId,
                    adapter.Name,
                    adapter.Vendor,
                    InferenceBackend.DirectMl,
                    adapter.Index,
                    adapter.VramMb,
                    DriverVersion: nvidiaMatch?.DriverVersion,
                    RuntimeAvailable: runtime.DirectMlAvailable,
                    runtime.DirectMlAvailable ? null : "DirectML Execution Provider no está instalado."));
            }
        }
        catch (Exception exception)
        {
            warnings.Add($"No fue posible enumerar adaptadores DXGI: {exception.Message}");
        }

        foreach (var gpu in nvidiaDevices)
        {
            devices.Add(new DeviceInfo(
                $"cuda:{gpu.Uuid.ToLowerInvariant()}",
                gpu.Name,
                HardwareVendor.Nvidia,
                InferenceBackend.Cuda,
                gpu.Index,
                gpu.VramMb,
                gpu.DriverVersion,
                runtime.CudaAvailable,
                runtime.CudaAvailable ? null : "CUDA Execution Provider no está instalado."));
        }

        return new HardwareInventory(devices, runtime, warnings);
    }
}
