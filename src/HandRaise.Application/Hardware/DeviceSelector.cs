namespace HandRaise.Application.Hardware;

public static class DeviceSelector
{
    public static DeviceInfo RecommendDevice(IEnumerable<DeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var candidates = devices.ToArray();

        return candidates
            .OrderByDescending(Score)
            .ThenByDescending(device => device.VramMb ?? 0)
            .ThenBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("El inventario no contiene ningún dispositivo.");
    }

    public static DeviceSelection ResolveModeOrDevice(
        IEnumerable<DeviceInfo> devices,
        string? mode,
        string? savedDeviceId)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var candidates = devices.ToArray();
        var cpu = candidates.FirstOrDefault(device => device.Backend == InferenceBackend.Cpu)
            ?? throw new InvalidOperationException("El inventario debe incluir CPU.");

        if (string.Equals(mode, "cpu", StringComparison.OrdinalIgnoreCase))
        {
            return new DeviceSelection(cpu, false, null);
        }

        if (string.Equals(mode, "gpu", StringComparison.OrdinalIgnoreCase))
        {
            var bestGpu = candidates.Where(d => d.Backend != InferenceBackend.Cpu && d.RuntimeAvailable)
                .OrderByDescending(Score)
                .ThenByDescending(d => d.VramMb ?? 0)
                .FirstOrDefault();

            if (bestGpu is not null)
            {
                return new DeviceSelection(bestGpu, false, null);
            }

            return new DeviceSelection(cpu, true, "Modo GPU solicitado pero no hay acelerador DirectML disponible. Se usará CPU.");
        }

        return ResolveSavedDevice(candidates, savedDeviceId);
    }

    public static DeviceSelection ResolveSavedDevice(
        IEnumerable<DeviceInfo> devices,
        string? savedDeviceId)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var candidates = devices.ToArray();
        var cpu = candidates.FirstOrDefault(device => device.Backend == InferenceBackend.Cpu)
            ?? throw new InvalidOperationException("El inventario debe incluir CPU.");

        if (string.IsNullOrWhiteSpace(savedDeviceId))
        {
            return new DeviceSelection(RecommendDevice(candidates), false, null);
        }

        var saved = candidates.FirstOrDefault(
            device => string.Equals(device.Id, savedDeviceId, StringComparison.OrdinalIgnoreCase));

        if (saved is { RuntimeAvailable: true })
        {
            return new DeviceSelection(saved, false, null);
        }

        var reason = saved is null
            ? "ya no existe"
            : saved.UnavailableReason ?? "su runtime no está disponible";
        var warning = $"El dispositivo guardado '{savedDeviceId}' {reason}. Se usará CPU.";
        return new DeviceSelection(cpu, true, warning);
    }

    private static int Score(DeviceInfo device)
    {
        if (!device.RuntimeAvailable)
        {
            return -1;
        }

        return (device.Vendor, device.Backend) switch
        {
            (HardwareVendor.Nvidia, InferenceBackend.Cuda) => 400,
            (_, InferenceBackend.DirectMl) => 300,
            (_, InferenceBackend.Cuda) => 200,
            (_, InferenceBackend.Cpu) => 100,
            _ => 0
        };
    }
}
