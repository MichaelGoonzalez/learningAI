using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HandRaise.Application.Storage;
using HandRaise.Application.Zones;
using HandRaise.Host.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HandRaise.Host.Tests;

public sealed class CameraManagementApiTests
{
    [Fact]
    public async Task CrudNeverReturnsRtspCredentials()
    {
        var manager = new FakeCameraManager();
        await using var app = CreateApp(manager, "secret");
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");
        var body = new CameraWriteRequest("gate", "Gate", "rtsp://camera/live", false, false, "admin", "very-secret");

        var created = await client.PostAsJsonAsync("/api/v1/cameras", body);
        var listed = await client.GetStringAsync("/api/v1/cameras");
        var updated = await client.PutAsJsonAsync("/api/v1/cameras/gate", body with { Name = "Gate 2" });
        var deleted = await client.DeleteAsync("/api/v1/cameras/gate");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var responses = (await created.Content.ReadAsStringAsync()) + listed + (await updated.Content.ReadAsStringAsync());
        Assert.DoesNotContain("admin", responses, StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret", responses, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidZonesReturnProblemDetailsAndMissingCameraReturns404()
    {
        var manager = new FakeCameraManager();
        await using var app = CreateApp(manager, "secret");
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");
        var invalid = new[] { new NormalizedZone("bad", [new(0, 0), new(1, 1)]) };

        var bad = await client.PutAsJsonAsync("/api/v1/cameras/missing/zones", invalid);
        var missing = await client.GetAsync("/api/v1/cameras/missing");
        var snapshot = await client.GetAsync("/api/v1/cameras/missing/snapshot");

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("application/problem+json", bad.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, snapshot.StatusCode);
    }

    [Fact]
    public async Task WritesRequireConfiguredApiKey()
    {
        await using var app = CreateApp(new FakeCameraManager(), null);
        await app.StartAsync();
        var response = await app.GetTestClient().PostAsJsonAsync("/api/v1/cameras",
            new CameraWriteRequest("cam", "Cam", "0"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CredentialFileDoesNotContainPlainText()
    {
        var root = Path.Combine(Path.GetTempPath(), $"handraise-{Guid.NewGuid():N}");
        var path = Path.Combine(root, "credentials.json");
        try
        {
            using var store = new JsonCameraCredentialStore(path, new XorProtector());
            await store.SaveAsync("cam", new CameraCredentials("admin", "very-secret"));
            Assert.DoesNotContain("admin", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
            Assert.Equal("very-secret", (await store.GetAsync("cam"))!.Password);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static WebApplication CreateApp(FakeCameraManager manager, string? apiKey) =>
        HostApplication.Build([], startCameras: false, builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Environment.EnvironmentName = "Production";
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["api:apiKey"] = apiKey, ["nodeId"] = "node-test", ["siteId"] = "site-test",
                ["storage:snapshotDirectory"] = Path.GetTempPath()
            });
            builder.Services.AddSingleton<ICameraManagementService>(manager);
            builder.Services.AddSingleton<IHandEventRepository>(new ReadApiTests.FakeRepositoryForD2());
        });

    private sealed class FakeCameraManager : ICameraManagementService
    {
        public event Action<string, bool>? CameraRunningStateChanged;
        public event Action? CamerasChanged;

        private readonly Dictionary<string, CameraView> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<NormalizedZone>> _zones = new(StringComparer.OrdinalIgnoreCase);
        public Task<IReadOnlyList<CameraView>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<CameraView>>(_values.Values.ToArray());
        public Task<CameraView?> GetAsync(string id, CancellationToken token = default) => Task.FromResult(_values.GetValueOrDefault(id));
        public Task<CameraView> CreateAsync(CameraWriteRequest request, CancellationToken token = default) { var value = View(request, request.Id); _values.Add(value.Id, value); _zones[value.Id] = []; return Task.FromResult(value); }
        public Task<CameraView?> UpdateAsync(string id, CameraWriteRequest request, CancellationToken token = default) { if (!_values.ContainsKey(id)) return Task.FromResult<CameraView?>(null); var value = View(request, id); _values[id] = value; return Task.FromResult<CameraView?>(value); }
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) => Task.FromResult(_values.Remove(id));
        public Task<CameraView?> StartAsync(string id, CancellationToken token = default) => Task.FromResult(_values.TryGetValue(id, out var value) ? value with { Enabled = true, Running = true } : null);
        public Task<CameraView?> StopAsync(string id, CancellationToken token = default) => Task.FromResult(_values.TryGetValue(id, out var value) ? value with { Enabled = false, Running = false } : null);
        public Task<CameraTestResult> TestAsync(CameraTestRequest request, CancellationToken token = default) => Task.FromResult(new CameraTestResult(true, null, 640, 480, 30, 10));
        public Task<byte[]?> GetSnapshotAsync(string id, CancellationToken token = default) => Task.FromResult<byte[]?>(null);
        public Task<IReadOnlyList<NormalizedZone>?> GetZonesAsync(string id, CancellationToken token = default) => Task.FromResult(_zones.TryGetValue(id, out var value) ? value : null);
        public Task<bool> UpdateZonesAsync(string id, IReadOnlyList<NormalizedZone> zones, CancellationToken token = default) { ZoneEditorLogic.Validate(zones); if (!_values.ContainsKey(id)) return Task.FromResult(false); _zones[id] = zones; return Task.FromResult(true); }
        public Task<DeviceChangeResult> ChangeDeviceAsync(string deviceId, CancellationToken token = default) => Task.FromResult(new DeviceChangeResult(true, deviceId, null, 1));
        public Task<CameraStreamSubscriptionResult> SubscribeStreamAsync(string id, double fps, int quality, CancellationToken token = default) =>
            Task.FromResult(_values.ContainsKey(id) ? new CameraStreamSubscriptionResult(CameraStreamStatus.Success) : new CameraStreamSubscriptionResult(CameraStreamStatus.NotFound));
        private static CameraView View(CameraWriteRequest request, string id) => new(id, request.Name, request.Source, request.Enabled, false, false, 0, null);
    }

    private sealed class XorProtector : IDataProtector
    {
        public byte[] Protect(byte[] value) => value.Select(x => (byte)(x ^ 0xA5)).ToArray();
        public byte[] Unprotect(byte[] value) => Protect(value);
    }
}
