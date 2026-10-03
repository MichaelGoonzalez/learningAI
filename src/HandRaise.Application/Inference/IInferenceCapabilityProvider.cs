using HandRaise.Domain.Analytics;

namespace HandRaise.Application.Inference;

public interface IInferenceCapabilityProvider
{
    string ProviderId { get; }
    string ModelId { get; }
    string ModelVersion { get; }
    IReadOnlyList<InferenceCapability> Capabilities { get; }
    string? PreferredDevice { get; }
    string RuntimeStatus { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
}

