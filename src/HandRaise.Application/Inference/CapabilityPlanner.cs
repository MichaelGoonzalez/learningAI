using HandRaise.Application.Analytics;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Models;

namespace HandRaise.Application.Inference;

public sealed class CapabilityPlanner : ICapabilityPlanner
{
    private readonly IModelRegistry _registry;

    public CapabilityPlanner(IModelRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public CapabilityExecutionPlan CreatePlan(
        string cameraId,
        IReadOnlyList<CameraAnalyticInstance> activeAnalytics,
        IAnalyticCatalog catalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cameraId);
        ArgumentNullException.ThrowIfNull(catalog);

        var enabledAnalytics = activeAnalytics?
            .Where(a => a.Enabled && a.Status != AnalyticStatus.Inactive && a.Status != AnalyticStatus.Error)
            .ToList() ?? [];

        if (enabledAnalytics.Count == 0)
        {
            return new CapabilityExecutionPlan(
                CameraId: cameraId,
                RequiredCapabilities: [],
                SelectedProviders: [],
                ActiveAnalyticsCount: 0,
                DependentAnalyticIds: []);
        }

        var requiredCapabilities = new HashSet<InferenceCapability>();
        var dependentAnalyticIds = new List<string>();

        foreach (var instance in enabledAnalytics)
        {
            dependentAnalyticIds.Add(instance.Id);
            var definition = catalog.GetById(instance.AnalyticTypeId);
            if (definition != null && definition.RequiredCapabilities != null)
            {
                foreach (var cap in definition.RequiredCapabilities)
                {
                    requiredCapabilities.Add(cap);
                }
            }
        }

        var selectedProviders = new List<SelectedProviderInfo>();
        foreach (var custom in enabledAnalytics.Where(a => CustomObjectEvaluator.IsCustom(a.AnalyticTypeId))
                     .Select(a => new CustomObjectEvaluator(a)).DistinctBy(a => a.ModelId))
        {
            var model = _registry.GetModel(custom.ModelId);
            if (model?.CustomTraining != null)
                selectedProviders.Add(new("custom-object-runtime", model.Id, model.DisplayName + " · " + model.Version,
                    [InferenceCapability.ObjectDetection], "cpu"));
        }
        var remainingCaps = new HashSet<InferenceCapability>(enabledAnalytics.Where(a => !CustomObjectEvaluator.IsCustom(a.AnalyticTypeId))
            .SelectMany(a => catalog.GetById(a.AnalyticTypeId)?.RequiredCapabilities ?? []));
        var availableProviders = _registry.GetProviders();

        // Greedily select provider covering the most remaining capabilities
        while (remainingCaps.Count > 0)
        {
            IInferenceCapabilityProvider? bestProvider = null;
            var bestCovered = new List<InferenceCapability>();

            foreach (var provider in availableProviders)
            {
                var covered = provider.Capabilities.Where(c => remainingCaps.Contains(c)).ToList();
                if (covered.Count > bestCovered.Count)
                {
                    bestProvider = provider;
                    bestCovered = covered;
                }
            }

            if (bestProvider == null || bestCovered.Count == 0)
            {
                // No provider found for some capabilities (e.g. Pure Tracking handled in software)
                break;
            }

            var modelDesc = _registry.GetModel(bestProvider.ModelId);
            selectedProviders.Add(new SelectedProviderInfo(
                ProviderId: bestProvider.ProviderId,
                ModelId: bestProvider.ModelId,
                ModelName: modelDesc?.DisplayName ?? bestProvider.ModelId,
                CoveredCapabilities: bestCovered,
                Device: bestProvider.PreferredDevice));

            foreach (var cap in bestCovered)
            {
                remainingCaps.Remove(cap);
            }
        }

        return new CapabilityExecutionPlan(
            CameraId: cameraId,
            RequiredCapabilities: requiredCapabilities.ToArray(),
            SelectedProviders: selectedProviders,
            ActiveAnalyticsCount: enabledAnalytics.Count,
            DependentAnalyticIds: dependentAnalyticIds);
    }
}
