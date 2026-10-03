using HandRaise.Application.Hardware;
using HandRaise.Application.Inference;

namespace HandRaise.Infrastructure.Windows.Inference;

public sealed class WindowsMlBackendFactory : IInferenceBackendFactory
{
    public IInferenceBackend Create(DeviceInfo device) => new WindowsMlOnnxBackend(device);
}
