using HandRaise.Application.Hardware;

namespace HandRaise.Application.Inference;

public interface IInferenceBackend : IAsyncDisposable
{
    DeviceInfo Device { get; }

    InferenceExecutionInfo ExecutionInfo { get; }

    ValueTask LoadAsync(ModelDescriptor model, CancellationToken cancellationToken = default);

    ValueTask<PoseBatch> InferAsync(ImageFrame frame, CancellationToken cancellationToken = default);
}
