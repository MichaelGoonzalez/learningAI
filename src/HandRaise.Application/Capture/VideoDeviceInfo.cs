namespace HandRaise.Application.Capture;

public sealed record VideoDeviceInfo(
    int Index,
    string Name,
    string DeviceId,
    bool IsDefault = false)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"Cámara USB {Index}" : $"{Name} ({Index})";
}

public interface IVideoDeviceEnumerator
{
    Task<IReadOnlyList<VideoDeviceInfo>> EnumerateDevicesAsync(CancellationToken token = default);
}
