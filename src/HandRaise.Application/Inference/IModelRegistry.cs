using HandRaise.Domain.Analytics;
using HandRaise.Domain.Models;

namespace HandRaise.Application.Inference;

public interface IModelRegistry
{
    IReadOnlyList<ModelDescriptor> ListModels();
    ModelDescriptor? GetModel(string id);
    IReadOnlyList<IInferenceCapabilityProvider> GetProviders();
    IInferenceCapabilityProvider? ResolveProvider(InferenceCapability capability);
    void RegisterModel(ModelDescriptor model, IInferenceCapabilityProvider? provider = null);
    bool UnregisterModel(string id);
}

