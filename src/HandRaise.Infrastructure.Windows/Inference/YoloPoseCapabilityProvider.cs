using HandRaise.Application.Inference;
using HandRaise.Domain.Analytics;

namespace HandRaise.Infrastructure.Windows.Inference;

public sealed class YoloPoseCapabilityProvider : IInferenceCapabilityProvider
{
    private readonly IInferenceBackend? _backend;
    private string _runtimeStatus = "ready";

    public string ProviderId { get; }
    public string ModelId { get; }
    public string ModelVersion { get; }
    public IReadOnlyList<InferenceCapability> Capabilities { get; }
    public string? PreferredDevice { get; }
    public string RuntimeStatus => _runtimeStatus;

    public YoloPoseCapabilityProvider(
        string providerId = "yolo-pose-provider",
        string modelId = "yolo26n-pose",
        string modelVersion = "1.0.0",
        IReadOnlyList<InferenceCapability>? capabilities = null,
        string? preferredDevice = "gpu",
        IInferenceBackend? backend = null)
    {
        ProviderId = providerId;
        ModelId = modelId;
        ModelVersion = modelVersion;
        Capabilities = capabilities ?? [
            InferenceCapability.PoseEstimation,
            InferenceCapability.ObjectDetection,
            InferenceCapability.Tracking
        ];
        PreferredDevice = preferredDevice;
        _backend = backend;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _runtimeStatus = "active";
        return Task.CompletedTask;
    }
}

