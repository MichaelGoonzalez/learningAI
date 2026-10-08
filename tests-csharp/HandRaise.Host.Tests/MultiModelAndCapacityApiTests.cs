using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HandRaise.Application.Analytics;
using HandRaise.Application.Inference;
using HandRaise.Application.Storage;
using HandRaise.Application.Zones;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Models;
using HandRaise.Host.Api;
using HandRaise.Host.Services;
using HandRaise.Infrastructure.Windows.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HandRaise.Host.Tests;

public sealed class MultiModelAndCapacityApiTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _analyticStorePath;

    public MultiModelAndCapacityApiTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"multimodel_api_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _analyticStorePath = Path.Combine(_tempDirectory, "analytics.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            try
            {
                Directory.Delete(_tempDirectory, true);
            }
            catch
            {
                // Best effort cleanup in tests
            }
        }
    }

    [Fact]
    public async Task GetModels_RequiresAuthentication()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetModels_ReturnsRegisteredModels()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var response = await client.GetAsync("/api/v1/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var models = await response.Content.ReadFromJsonAsync<List<ModelDescriptor>>(AnalyticJsonDefaults.Options);
        Assert.NotNull(models);
        Assert.NotEmpty(models);

        var defaultModel = models.FirstOrDefault(m => m.Id == "yolo26n-pose");
        Assert.NotNull(defaultModel);
        Assert.Equal(ModelFormat.Onnx, defaultModel.Format);
        Assert.Contains(InferenceCapability.PoseEstimation, defaultModel.Capabilities);
        Assert.Contains(InferenceCapability.ObjectDetection, defaultModel.Capabilities);
        Assert.True(defaultModel.InputWidth > 0);
        Assert.True(defaultModel.InputHeight > 0);
    }

    [Fact]
    public async Task GetModelById_ExistingModel_Returns200AndModelDetails()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var response = await client.GetAsync("/api/v1/models/yolo26n-pose");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var model = await response.Content.ReadFromJsonAsync<ModelDescriptor>(AnalyticJsonDefaults.Options);
        Assert.NotNull(model);
        Assert.Equal("yolo26n-pose", model.Id);
        Assert.Equal("YOLO26 Nano Pose", model.DisplayName);
        Assert.Equal(ModelFormat.Onnx, model.Format);
        Assert.True(model.Enabled);
    }

    [Fact]
    public async Task GetModelById_NonExistent_Returns404ProblemDetails()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var response = await client.GetAsync("/api/v1/models/non-existent-model-xyz");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRuntimePlan_NonExistentCamera_Returns404ProblemDetails()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var response = await client.GetAsync("/api/v1/cameras/cam-missing/runtime-plan");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetRuntimePlan_CameraWithNoAnalytics_ReturnsEmptyPlan()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var response = await client.GetAsync("/api/v1/cameras/cam-1/runtime-plan");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var plan = await response.Content.ReadFromJsonAsync<CapabilityExecutionPlan>(AnalyticJsonDefaults.Options);
        Assert.NotNull(plan);
        Assert.Equal("cam-1", plan.CameraId);
        Assert.Equal(0, plan.ActiveAnalyticsCount);
        Assert.Empty(plan.RequiredCapabilities);
        Assert.Empty(plan.SelectedProviders);
    }

    [Fact]
    public async Task GetRuntimePlan_CameraWithMultipleAnalytics_SharesSingleProvider()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        // Create multiple analytics requiring pose_estimation / object_detection / tracking
        var create1 = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            AnalyticTypeId: "hand_raise",
            Name: "Levantamiento de Mano",
            Enabled: true,
            Configuration: new Dictionary<string, object?> { ["consecutive_frames"] = 3 }));
        Assert.Equal(HttpStatusCode.Created, create1.StatusCode);

        var create2 = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            AnalyticTypeId: "person_presence",
            Name: "Presencia",
            Enabled: true,
            Configuration: new Dictionary<string, object?> { ["confidence_threshold"] = 0.5 }));
        Assert.Equal(HttpStatusCode.Created, create2.StatusCode);

        var create3 = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            AnalyticTypeId: "zone_intrusion",
            Name: "Intrusión",
            Enabled: true,
            AssignedZoneIds: ["zone-vault"],
            Configuration: new Dictionary<string, object?> { ["confidence_threshold"] = 0.5 }));
        Assert.Equal(HttpStatusCode.Created, create3.StatusCode);

        var response = await client.GetAsync("/api/v1/cameras/cam-1/runtime-plan");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var plan = await response.Content.ReadFromJsonAsync<CapabilityExecutionPlan>(AnalyticJsonDefaults.Options);
        Assert.NotNull(plan);
        Assert.Equal("cam-1", plan.CameraId);
        Assert.Equal(3, plan.ActiveAnalyticsCount);
        Assert.Contains(InferenceCapability.PoseEstimation, plan.RequiredCapabilities);
        Assert.Contains(InferenceCapability.ObjectDetection, plan.RequiredCapabilities);
        Assert.Contains(InferenceCapability.Tracking, plan.RequiredCapabilities);

        // CapabilityPlanner optimizes by selecting a single shared provider covering pose, detection, and tracking
        Assert.Single(plan.SelectedProviders);
        var provider = plan.SelectedProviders[0];
        Assert.Equal("yolo26n-pose", provider.ModelId);
        Assert.Equal("yolo-pose-provider", provider.ProviderId);
    }

    [Fact]
    public async Task GetSystemCapacity_RequiresAuthentication()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/v1/system/capacity");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSystemCapacity_ReturnsExtendedCapacitySnapshot()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var response = await client.GetAsync("/api/v1/system/capacity");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var capacity = await response.Content.ReadFromJsonAsync<ExtendedCapacitySnapshot>(AnalyticJsonDefaults.Options);
        Assert.NotNull(capacity);
        Assert.Equal("node-test", capacity.NodeId);
        Assert.Equal("site-test", capacity.SiteId);
        Assert.NotEmpty(capacity.Device);
        Assert.NotEmpty(capacity.ActiveModels);
        Assert.Contains("yolo26n-pose", capacity.ActiveModels);
        Assert.True(capacity.TargetFramesPerSecond > 0);
        Assert.Equal(CapacityStatus.Healthy, capacity.CapacityStatus);
    }

    private WebApplication CreateApp() =>
        HostApplication.Build([], startCameras: false, builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Environment.EnvironmentName = "Production";
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["api:apiKey"] = "secret",
                ["nodeId"] = "node-test",
                ["siteId"] = "site-test",
                ["storage:snapshotDirectory"] = _tempDirectory,
                ["storage:analyticStorePath"] = _analyticStorePath
            });
            builder.Services.AddSingleton<ICameraStore>(new FakeCameraStore());
            builder.Services.AddSingleton<ICameraManagementService>(new FakeCameraManagementService());
            builder.Services.AddSingleton<IHandEventRepository>(new ReadApiTests.FakeRepositoryForD2());
        });

    private sealed class FakeCameraStore : ICameraStore
    {
        private readonly Dictionary<string, CameraDefinition> _cameras = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cam-1"] = new CameraDefinition("cam-1", "Cam 1", "0", true, false, [
                new NormalizedZone("zone-vault", [new(0, 0), new(0.5, 0), new(0.5, 0.5), new(0, 0.5)])
            ])
        };

        public Task InitializeAsync(IReadOnlyList<CameraDefinition> seed, CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CameraDefinition>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<CameraDefinition>>(_cameras.Values.ToArray());
        public Task<CameraDefinition?> GetAsync(string id, CancellationToken token = default) => Task.FromResult(_cameras.GetValueOrDefault(id));
        public Task AddAsync(CameraDefinition camera, CancellationToken token = default) { _cameras[camera.Id] = camera; return Task.CompletedTask; }
        public Task<bool> UpdateAsync(CameraDefinition camera, CancellationToken token = default) { _cameras[camera.Id] = camera; return Task.FromResult(true); }
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) => Task.FromResult(_cameras.Remove(id));
    }

    private sealed class FakeCameraManagementService : ICameraManagementService
    {
        public event Action<string, bool>? CameraRunningStateChanged;
        public event Action? CamerasChanged;

        private readonly Dictionary<string, CameraView> _cameras = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cam-1"] = new CameraView("cam-1", "Cam 1", "0", true, true, true, 30.0, null)
        };

        public Task<IReadOnlyList<CameraView>> ListAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<CameraView>>(_cameras.Values.ToArray());

        public Task<CameraView?> GetAsync(string id, CancellationToken token = default) =>
            Task.FromResult(_cameras.GetValueOrDefault(id));

        public Task<CameraView> CreateAsync(CameraWriteRequest request, CancellationToken token = default) =>
            throw new NotImplementedException();
        public Task<CameraView?> UpdateAsync(string id, CameraWriteRequest request, CancellationToken token = default) =>
            throw new NotImplementedException();
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) =>
            throw new NotImplementedException();
        public Task<CameraView?> StartAsync(string id, CancellationToken token = default) =>
            throw new NotImplementedException();
        public Task<CameraView?> StopAsync(string id, CancellationToken token = default) =>
            throw new NotImplementedException();
        public Task<CameraTestResult> TestAsync(CameraTestRequest request, CancellationToken token = default) =>
            throw new NotImplementedException();
        public Task<byte[]?> GetSnapshotAsync(string id, CancellationToken token = default) =>
            Task.FromResult<byte[]?>(null);
        public Task<IReadOnlyList<NormalizedZone>?> GetZonesAsync(string id, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<NormalizedZone>?>(null);
        public Task<bool> UpdateZonesAsync(string id, IReadOnlyList<NormalizedZone> zones, CancellationToken token = default) =>
            Task.FromResult(true);
        public Task<DeviceChangeResult> ChangeDeviceAsync(string deviceId, CancellationToken token = default) =>
            Task.FromResult(new DeviceChangeResult(true, deviceId, null, 0));
        public Task<CameraStreamSubscriptionResult> SubscribeStreamAsync(string id, double fps, int quality, CancellationToken token = default) =>
            Task.FromResult(new CameraStreamSubscriptionResult(CameraStreamStatus.Success));
    }
}

