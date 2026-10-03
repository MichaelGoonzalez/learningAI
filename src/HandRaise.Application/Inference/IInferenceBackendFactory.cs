using HandRaise.Application.Hardware;

namespace HandRaise.Application.Inference;

public interface IInferenceBackendFactory
{
    IInferenceBackend Create(DeviceInfo device);
}
