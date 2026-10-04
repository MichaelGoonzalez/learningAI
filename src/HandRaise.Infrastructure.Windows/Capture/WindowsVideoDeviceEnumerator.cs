using HandRaise.Application.Capture;
using Windows.Devices.Enumeration;

namespace HandRaise.Infrastructure.Windows.Capture;

public sealed class WindowsVideoDeviceEnumerator : IVideoDeviceEnumerator
{
    public async Task<IReadOnlyList<VideoDeviceInfo>> EnumerateDevicesAsync(CancellationToken token = default)
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            if (devices is null || devices.Count == 0)
            {
                return [new VideoDeviceInfo(0, "Cámara predeterminada (Índice 0)", "0", true)];
            }

            var list = new List<VideoDeviceInfo>(devices.Count);
            for (var i = 0; i < devices.Count; i++)
            {
                var dev = devices[i];
                var name = string.IsNullOrWhiteSpace(dev.Name) ? $"Cámara USB {i}" : dev.Name;
                list.Add(new VideoDeviceInfo(i, $"{name} (USB {i})", dev.Id, i == 0));
            }

            return list;
        }
        catch
        {
            return [new VideoDeviceInfo(0, "Cámara predeterminada (Índice 0)", "0", true)];
        }
    }
}
