namespace HandRaise.Application.Hardware;

public interface IDeviceDetector
{
    Task<HardwareInventory> DetectAsync(CancellationToken cancellationToken = default);
}
