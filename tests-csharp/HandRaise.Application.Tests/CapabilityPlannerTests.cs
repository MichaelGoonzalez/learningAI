using HandRaise.Application.Analytics;
using HandRaise.Application.Inference;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Models;
using Xunit;

namespace HandRaise.Application.Tests;

public class CapabilityPlannerTests
{
    private readonly StandardAnalyticCatalog _catalog = new();

    private sealed class FakeModelRegistry : IModelRegistry
    {
        private readonly List<ModelDescriptor> _models = [];
        private readonly List<IInferenceCapabilityProvider> _providers = [];

        public void RegisterModel(ModelDescriptor model, IInferenceCapabilityProvider? provider = null)
        {
            _models.Add(model);
            if (provider != null) _providers.Add(provider);
        }

        public IReadOnlyList<ModelDescriptor> ListModels() => _models;
        public ModelDescriptor? GetModel(string id) => _models.FirstOrDefault(m => m.Id == id);
        public IReadOnlyList<IInferenceCapabilityProvider> GetProviders() => _providers;
        public IInferenceCapabilityProvider? ResolveProvider(InferenceCapability capability) =>
            _providers.FirstOrDefault(p => p.Capabilities.Contains(capability));
        public bool UnregisterModel(string id) => _models.RemoveAll(m => m.Id == id) > 0;
    }

    private sealed class StubCapabilityProvider(
        string providerId,
        string modelId,
        IReadOnlyList<InferenceCapability> capabilities,
        string? device = "gpu") : IInferenceCapabilityProvider
    {
        public string ProviderId => providerId;
        public string ModelId => modelId;
        public string ModelVersion => "1.0.0";
        public IReadOnlyList<InferenceCapability> Capabilities => capabilities;
        public string? PreferredDevice => device;
        public string RuntimeStatus => "ready";
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public void CreatePlan_NoActiveAnalytics_ReturnsEmptyPlan()
    {
        var registry = new FakeModelRegistry();
        var planner = new CapabilityPlanner(registry);

        var plan = planner.CreatePlan("cam-1", [], _catalog);

        Assert.Equal("cam-1", plan.CameraId);
        Assert.Empty(plan.RequiredCapabilities);
        Assert.Empty(plan.SelectedProviders);
        Assert.Equal(0, plan.ActiveAnalyticsCount);
        Assert.Empty(plan.DependentAnalyticIds);
    }

    [Fact]
    public void CreatePlan_HandRaiseAnalytic_SelectsPoseProviderCoveringAllRequiredCapabilities()
    {
        var registry = new FakeModelRegistry();
        var model = new ModelDescriptor(
            Id: "yolo26n-pose",
            DisplayName: "YOLO26 Nano Pose",
            Version: "1.0.0",
            Format: ModelFormat.Onnx,
            Capabilities: [InferenceCapability.PoseEstimation, InferenceCapability.ObjectDetection, InferenceCapability.Tracking],
            InputWidth: 640,
            InputHeight: 640,
            Path: "models/yolo26n-pose.onnx");

        var provider = new StubCapabilityProvider("pose-prov", "yolo26n-pose", [
            InferenceCapability.PoseEstimation,
            InferenceCapability.ObjectDetection,
            InferenceCapability.Tracking
        ]);

        registry.RegisterModel(model, provider);
        var planner = new CapabilityPlanner(registry);

        var instance = new CameraAnalyticInstance(
            Id: "hr-1",
            CameraId: "cam-1",
            AnalyticTypeId: "hand_raise",
            Name: "Hand Raise",
            Enabled: true,
            Status: AnalyticStatus.Active);

        var plan = planner.CreatePlan("cam-1", [instance], _catalog);

        Assert.Equal("cam-1", plan.CameraId);
        Assert.Equal(1, plan.ActiveAnalyticsCount);
        Assert.Single(plan.SelectedProviders);
        Assert.Equal("pose-prov", plan.SelectedProviders[0].ProviderId);
        Assert.Equal("yolo26n-pose", plan.SelectedProviders[0].ModelId);
        Assert.Contains(InferenceCapability.PoseEstimation, plan.SelectedProviders[0].CoveredCapabilities);
    }

    [Fact]
    public void CreatePlan_AllFiveAnalytics_SharesSingleProviderWithoutDuplication()
    {
        var registry = new FakeModelRegistry();
        var model = new ModelDescriptor(
            Id: "yolo26n-pose",
            DisplayName: "YOLO26 Nano Pose",
            Version: "1.0.0",
            Format: ModelFormat.Onnx,
            Capabilities: [InferenceCapability.PoseEstimation, InferenceCapability.ObjectDetection, InferenceCapability.Tracking],
            InputWidth: 640,
            InputHeight: 640,
            Path: "models/yolo26n-pose.onnx");

        var provider = new StubCapabilityProvider("pose-prov", "yolo26n-pose", [
            InferenceCapability.PoseEstimation,
            InferenceCapability.ObjectDetection,
            InferenceCapability.Tracking
        ]);

        registry.RegisterModel(model, provider);
        var planner = new CapabilityPlanner(registry);

        var instances = new[]
        {
            new CameraAnalyticInstance("i1", "cam-1", "hand_raise", "HR", true, AnalyticStatus.Active),
            new CameraAnalyticInstance("i2", "cam-1", "person_presence", "PP", true, AnalyticStatus.Active),
            new CameraAnalyticInstance("i3", "cam-1", "zone_intrusion", "ZI", true, AnalyticStatus.Active),
            new CameraAnalyticInstance("i4", "cam-1", "line_crossing", "LC", true, AnalyticStatus.Active),
            new CameraAnalyticInstance("i5", "cam-1", "person_counting", "PC", true, AnalyticStatus.Active),
        };

        var plan = planner.CreatePlan("cam-1", instances, _catalog);

        Assert.Equal(5, plan.ActiveAnalyticsCount);
        Assert.Single(plan.SelectedProviders); // Shared single provider
        Assert.Equal(5, plan.DependentAnalyticIds.Count);
    }

    [Fact]
    public void CreatePlan_InactiveOrDisabledInstances_AreExcludedFromPlan()
    {
        var registry = new FakeModelRegistry();
        var planner = new CapabilityPlanner(registry);

        var instances = new[]
        {
            new CameraAnalyticInstance("i1", "cam-1", "hand_raise", "HR", true, AnalyticStatus.Inactive),
            new CameraAnalyticInstance("i2", "cam-1", "person_presence", "PP", false, AnalyticStatus.Active),
            new CameraAnalyticInstance("i3", "cam-1", "zone_intrusion", "ZI", true, AnalyticStatus.Error),
        };

        var plan = planner.CreatePlan("cam-1", instances, _catalog);

        Assert.Equal(0, plan.ActiveAnalyticsCount);
        Assert.Empty(plan.SelectedProviders);
        Assert.Empty(plan.DependentAnalyticIds);
    }
}

