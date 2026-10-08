using HandRaise.Application.Analytics;
using HandRaise.Application.Inference;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Models;
using HandRaise.Host.Services;
using HandRaise.Infrastructure.Windows.Inference;
using HandRaise.Infrastructure.Windows.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace HandRaise.Host.Tests;

public sealed class CustomModelSelectionTests
{
    [Fact]
    public async Task SelectionPersistsVersionAndMissingModelCanBeDisabledOrRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), "edge-model-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var registry = new StandardModelRegistry();
            var model = new ModelDescriptor("custom-birds-v2", "Aves", "2", ModelFormat.Onnx, [InferenceCapability.ObjectDetection],
                640, 640, "fixture", ClassCount: 1, KeypointCount: 0)
            { CustomTraining = new(Guid.NewGuid(), Guid.NewGuid(), "", [new(Guid.NewGuid(), "Pollo")], DateTimeOffset.UtcNow, "base", "snapshot", "fake", new Dictionary<string, double>()) };
            registry.RegisterModel(model);
            using var provider = new ServiceCollection().AddSingleton<IModelRegistry>(registry).BuildServiceProvider();
            using var store = new JsonAnalyticInstanceStore(Path.Combine(root, "analytics.json"));
            var lines = new JsonLineStore(Path.Combine(root, "lines.json"));
            var service = new AnalyticManagementService(new Cameras(), store, new StandardAnalyticCatalog(), lines, provider);
            var selected = await service.CreateAsync("camera", new(AnalyticTypeId: CustomObjectEvaluator.TypeId,
                Configuration: new Dictionary<string, object?> { ["model_id"] = model.Id, ["confidence_threshold"] = .8 }));
            using var reloaded = new JsonAnalyticInstanceStore(Path.Combine(root, "analytics.json"));
            var instance = Assert.Single(await reloaded.GetByCameraIdAsync("camera"));
            var evaluator = new CustomObjectEvaluator(instance);
            Assert.Equal(model.Id, evaluator.ModelId); Assert.Equal(.8, evaluator.Confidence);
            Assert.Equal("2", registry.GetModel(evaluator.ModelId)!.Version);
            registry.UnregisterModel(model.Id);
            await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync("other", new(AnalyticTypeId: CustomObjectEvaluator.TypeId,
                Configuration: new Dictionary<string, object?> { ["model_id"] = model.Id })));
            var disabled = await service.UpdateAsync("camera", selected.Id, new(Enabled: false));
            Assert.False(disabled!.Enabled);
            await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync("camera", selected.Id, new(Enabled: true)));
            Assert.True(await service.DeleteAsync("camera", selected.Id));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Cameras : ICameraStore
    {
        public Task InitializeAsync(IReadOnlyList<CameraDefinition> seed, CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CameraDefinition>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<CameraDefinition>>([]);
        public Task<CameraDefinition?> GetAsync(string id, CancellationToken token = default) => Task.FromResult<CameraDefinition?>(new(id, id, "fixture.mp4", false, false, []));
        public Task AddAsync(CameraDefinition camera, CancellationToken token = default) => Task.CompletedTask;
        public Task<bool> UpdateAsync(CameraDefinition camera, CancellationToken token = default) => Task.FromResult(true);
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) => Task.FromResult(true);
    }
}
