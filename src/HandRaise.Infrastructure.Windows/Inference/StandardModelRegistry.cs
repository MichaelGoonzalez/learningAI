using System.Collections.Concurrent;
using HandRaise.Application.Inference;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Models;

namespace HandRaise.Infrastructure.Windows.Inference;

public sealed class StandardModelRegistry : IModelRegistry
{
    private readonly ConcurrentDictionary<string, ModelDescriptor> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IInferenceCapabilityProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public StandardModelRegistry()
    {
    }

    public IReadOnlyList<ModelDescriptor> ListModels()
    {
        return _models.Values.ToList();
    }

    public ModelDescriptor? GetModel(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        _models.TryGetValue(id, out var model);
        return model;
    }

    public IReadOnlyList<IInferenceCapabilityProvider> GetProviders()
    {
        return _providers.Values.ToList();
    }

    public IInferenceCapabilityProvider? ResolveProvider(InferenceCapability capability)
    {
        return _providers.Values.FirstOrDefault(p => p.Capabilities.Contains(capability));
    }

    public void RegisterModel(ModelDescriptor model, IInferenceCapabilityProvider? provider = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Validate();

        _models[model.Id] = model;

        if (provider != null)
        {
            _providers[provider.ProviderId] = provider;
        }
    }

    public bool UnregisterModel(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var removed = _models.TryRemove(id, out _);
        
        var matchingProviders = _providers.Values.Where(p => string.Equals(p.ModelId, id, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var p in matchingProviders)
        {
            _providers.TryRemove(p.ProviderId, out _);
        }

        return removed;
    }
}

