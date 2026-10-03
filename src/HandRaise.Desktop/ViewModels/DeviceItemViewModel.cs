using HandRaise.Application.Hardware;

namespace HandRaise.Desktop.ViewModels;

public sealed class DeviceItemViewModel(DeviceInfo device)
{
    public DeviceInfo Device { get; } = device;
    public string Id => Device.Id;
    public string Name => Device.Name;
    public string DisplayName => Device.RuntimeAvailable
        ? $"{Device.Name} · {Device.Backend}"
        : $"{Device.Name} · no disponible: {Device.UnavailableReason ?? "runtime ausente"}";
    public bool IsAvailable => Device.RuntimeAvailable;
}

