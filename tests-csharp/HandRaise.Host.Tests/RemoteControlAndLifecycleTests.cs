using System.Net;
using System.Net.Http.Json;
using HandRaise.Application.Storage;
using HandRaise.Application.Zones;
using HandRaise.Host.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HandRaise.Host.Tests;

public sealed class RemoteControlAndLifecycleTests
{
    private sealed class LifecycleFakeCameraManager : ICameraManagementService
    {
        public event Action<string, bool>? CameraRunningStateChanged;
        public event Action? CamerasChanged;

        private readonly Dictionary<string, CameraView> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<NormalizedZone>> _zones = new(StringComparer.OrdinalIgnoreCase);

        public List<(string CameraId, bool IsRunning)> EventLog { get; } = [];

        public Task<IReadOnlyList<CameraView>> ListAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<CameraView>>(_values.Values.ToArray());

        public Task<CameraView?> GetAsync(string id, CancellationToken token = default) =>
            Task.FromResult(_values.GetValueOrDefault(id));

        public Task<CameraView> CreateAsync(CameraWriteRequest request, CancellationToken token = default)
        {
            var value = new CameraView(request.Id, request.Name, request.Source, request.Enabled, request.Enabled, request.Enabled, 30.0, null);
            _values[value.Id] = value;
            _zones[value.Id] = [];
            CamerasChanged?.Invoke();
            return Task.FromResult(value);
        }

        public Task<CameraView?> UpdateAsync(string id, CameraWriteRequest request, CancellationToken token = default)
        {
            if (!_values.ContainsKey(id)) return Task.FromResult<CameraView?>(null);
            var value = new CameraView(id, request.Name, request.Source, request.Enabled, _values[id].Running, _values[id].Online, 30.0, null);
            _values[id] = value;
            CamerasChanged?.Invoke();
            return Task.FromResult<CameraView?>(value);
        }

        public Task<bool> DeleteAsync(string id, CancellationToken token = default)
        {
            var removed = _values.Remove(id);
            if (removed) CamerasChanged?.Invoke();
            return Task.FromResult(removed);
        }

        public Task<CameraView?> StartAsync(string id, CancellationToken token = default)
        {
            if (!_values.TryGetValue(id, out var value)) return Task.FromResult<CameraView?>(null);
            var updated = value with { Enabled = true, Running = true, Online = true, FramesPerSecond = 30.0 };
            _values[id] = updated;
            EventLog.Add((id, true));
            CameraRunningStateChanged?.Invoke(id, true);
            return Task.FromResult<CameraView?>(updated);
        }

        public Task<CameraView?> StopAsync(string id, CancellationToken token = default)
        {
            if (!_values.TryGetValue(id, out var value)) return Task.FromResult<CameraView?>(null);
            var updated = value with { Enabled = false, Running = false, Online = false, FramesPerSecond = 0.0 };
            _values[id] = updated;
            EventLog.Add((id, false));
            CameraRunningStateChanged?.Invoke(id, false);
            return Task.FromResult<CameraView?>(updated);
        }

        public Task<CameraTestResult> TestAsync(CameraTestRequest request, CancellationToken token = default) =>
            Task.FromResult(new CameraTestResult(true, null, 640, 480, 30, 10));

        public Task<byte[]?> GetSnapshotAsync(string id, CancellationToken token = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<IReadOnlyList<NormalizedZone>?> GetZonesAsync(string id, CancellationToken token = default) =>
            Task.FromResult(_zones.TryGetValue(id, out var value) ? value : null);

        public Task<bool> UpdateZonesAsync(string id, IReadOnlyList<NormalizedZone> zones, CancellationToken token = default)
        {
            if (!_values.ContainsKey(id)) return Task.FromResult(false);
            _zones[id] = zones;
            return Task.FromResult(true);
        }

        public Task<DeviceChangeResult> ChangeDeviceAsync(string deviceId, CancellationToken token = default) =>
            Task.FromResult(new DeviceChangeResult(true, deviceId, null, 1));

        public Task<CameraStreamSubscriptionResult> SubscribeStreamAsync(string id, double fps, int quality, CancellationToken token = default) =>
            Task.FromResult(_values.ContainsKey(id)
                ? new CameraStreamSubscriptionResult(CameraStreamStatus.Success)
                : new CameraStreamSubscriptionResult(CameraStreamStatus.NotFound));
    }

    private static WebApplication CreateApp(LifecycleFakeCameraManager manager, string? apiKey = "test-secret") =>
        HostApplication.Build([], startCameras: false, builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Environment.EnvironmentName = "Production";
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["api:apiKey"] = apiKey,
                ["nodeId"] = "node-test",
                ["siteId"] = "site-test",
                ["storage:snapshotDirectory"] = Path.GetTempPath()
            });
            builder.Services.AddSingleton<ICameraManagementService>(manager);
            builder.Services.AddSingleton<IHandEventRepository>(new ReadApiTests.FakeRepositoryForD2());
        });

    [Fact]
    public async Task RemoteStartAndStopEndpoints_UpdateCameraStateAndRaiseEvents()
    {
        var manager = new LifecycleFakeCameraManager();
        await using var app = CreateApp(manager, "test-key");
        await app.StartAsync();

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-key");

        // 1. Create a camera
        var createResponse = await client.PostAsJsonAsync("/api/v1/cameras", new CameraWriteRequest(
            Id: "cam-remote-1",
            Name: "Cámara Remota 1",
            Source: "0",
            Enabled: false));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        // 2. Start Camera via Remote POST /api/v1/cameras/{id}/start
        var startResponse = await client.PostAsync("/api/v1/cameras/cam-remote-1/start", null);
        Assert.Equal(HttpStatusCode.OK, startResponse.StatusCode);

        var startedCam = await manager.GetAsync("cam-remote-1");
        Assert.NotNull(startedCam);
        Assert.True(startedCam.Running);
        Assert.True(startedCam.Enabled);
        Assert.Contains(manager.EventLog, e => e.CameraId == "cam-remote-1" && e.IsRunning);

        // 3. Stop Camera via Remote POST /api/v1/cameras/{id}/stop
        var stopResponse = await client.PostAsync("/api/v1/cameras/cam-remote-1/stop", null);
        Assert.Equal(HttpStatusCode.OK, stopResponse.StatusCode);

        var stoppedCam = await manager.GetAsync("cam-remote-1");
        Assert.NotNull(stoppedCam);
        Assert.False(stoppedCam.Running);
        Assert.False(stoppedCam.Enabled);
        Assert.Contains(manager.EventLog, e => e.CameraId == "cam-remote-1" && !e.IsRunning);
    }

    [Fact]
    public async Task RemoteStartAndStop_NonExistingCamera_Returns404NotFound()
    {
        var manager = new LifecycleFakeCameraManager();
        await using var app = CreateApp(manager, "test-key");
        await app.StartAsync();

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "test-key");

        var startResponse = await client.PostAsync("/api/v1/cameras/non-existent-camera/start", null);
        Assert.Equal(HttpStatusCode.NotFound, startResponse.StatusCode);

        var stopResponse = await client.PostAsync("/api/v1/cameras/non-existent-camera/stop", null);
        Assert.Equal(HttpStatusCode.NotFound, stopResponse.StatusCode);
    }
}
