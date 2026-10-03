using HandRaise.Application.Hardware;

namespace HandRaise.Host.Services;

public sealed class HostRuntimeState
{
    private HardwareInventory _inventory = new([], RuntimeInfo.DevelopmentFallback, []);
    private string? _activeDeviceId;
    private string? _modelName;

    public HardwareInventory Inventory => Volatile.Read(ref _inventory);
    public string? ActiveDeviceId => Volatile.Read(ref _activeDeviceId);
    public string? ModelName => Volatile.Read(ref _modelName);

    public void Set(HardwareInventory inventory, string activeDeviceId, string modelName)
    {
        Volatile.Write(ref _inventory, inventory);
        Volatile.Write(ref _activeDeviceId, activeDeviceId);
        Volatile.Write(ref _modelName, modelName);
    }

    public void SetActiveDevice(string activeDeviceId) => Volatile.Write(ref _activeDeviceId, activeDeviceId);
}
