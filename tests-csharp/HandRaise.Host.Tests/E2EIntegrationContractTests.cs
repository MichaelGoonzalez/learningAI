using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using HandRaise.Application.Analytics;
using HandRaise.Application.Capture;
using HandRaise.Application.Events;
using HandRaise.Application.Inference;
using HandRaise.Application.Storage;
using HandRaise.Application.Zones;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Models;
using HandRaise.Domain.Notifications;
using HandRaise.Domain.Rules;
using HandRaise.Host;
using HandRaise.Host.Api;
using HandRaise.Host.Services;
using HandRaise.Infrastructure.Windows.Capture;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HandRaise.Host.Tests;

public sealed class E2EIntegrationContractTests : IDisposable
{
    private const string ApiKey = "e2e-secret-key-12345";
    private readonly string _tempDir;
    private readonly string _camerasPath;
    private readonly string _analyticsPath;
    private readonly string _rulesPath;
    private readonly string _alertsPath;
    private readonly string _linesPath;
    private readonly string _destinationsPath;
    private readonly string _policiesPath;
    private readonly string _attemptsPath;

    public E2EIntegrationContractTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"e2e_contract_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _camerasPath = Path.Combine(_tempDir, "cameras.json");
        _analyticsPath = Path.Combine(_tempDir, "analytics.json");
        _rulesPath = Path.Combine(_tempDir, "rules.json");
        _alertsPath = Path.Combine(_tempDir, "alerts.json");
        _linesPath = Path.Combine(_tempDir, "lines.json");
        _destinationsPath = Path.Combine(_tempDir, "destinations.json");
        _policiesPath = Path.Combine(_tempDir, "policies.json");
        _attemptsPath = Path.Combine(_tempDir, "attempts.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private WebApplication CreateTestApp(string? apiKey = ApiKey)
    {
        var cameraStoreAndManager = new TestMemoryCameraStoreAndManager();

        var app = HostApplication.Build([], startCameras: false, builder =>
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["api:apiKey"] = apiKey ?? string.Empty,
                ["api:port"] = "5080",
                ["api:bindAddress"] = "127.0.0.1",
                ["api:allowedOrigins:0"] = "http://localhost:3000",
                ["api:allowedOrigins:1"] = "http://127.0.0.1:3000",
                ["nodeId"] = "e2e-test-node",
                ["siteId"] = "e2e-test-site",
                ["storage:cameraStorePath"] = _camerasPath,
                ["storage:analyticStorePath"] = _analyticsPath,
                ["storage:rulesStorePath"] = _rulesPath,
                ["storage:alertsStorePath"] = _alertsPath,
                ["storage:linesStorePath"] = _linesPath,
                ["storage:notificationDestinationsStorePath"] = _destinationsPath,
                ["storage:notificationPoliciesStorePath"] = _policiesPath,
                ["storage:notificationAttemptsStorePath"] = _attemptsPath
            });
            builder.Services.AddSingleton<ICameraStore>(cameraStoreAndManager);
            builder.Services.AddSingleton<ICameraManagementService>(cameraStoreAndManager);
            builder.WebHost.UseTestServer();
        });
        return app;
    }

    [Fact]
    public async Task E2E_HealthAndCors_ValidatesCorrectly()
    {
        await using var app = CreateTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        // 1. OPTIONS preflight
        using var preflightReq = new HttpRequestMessage(HttpMethod.Options, "/api/v1/health");
        preflightReq.Headers.Add("Origin", "http://localhost:3000");
        preflightReq.Headers.Add("Access-Control-Request-Method", "GET");
        preflightReq.Headers.Add("Access-Control-Request-Headers", "X-Api-Key,Content-Type");
        using var preflightResp = await client.SendAsync(preflightReq);

        Assert.Equal(HttpStatusCode.NoContent, preflightResp.StatusCode);
        Assert.True(preflightResp.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("http://localhost:3000", preflightResp.Headers.GetValues("Access-Control-Allow-Origin").First());

        // 2. Health endpoint GET with auth
        using var healthReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        healthReq.Headers.Add("Origin", "http://localhost:3000");
        healthReq.Headers.Add("X-Api-Key", ApiKey);
        using var healthResp = await client.SendAsync(healthReq);

        Assert.Equal(HttpStatusCode.OK, healthResp.StatusCode);
        var json = await healthResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("healthy", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("e2e-test-node", doc.RootElement.GetProperty("node_id").GetString());
        Assert.Equal("e2e-test-site", doc.RootElement.GetProperty("site_id").GetString());
    }

    [Fact]
    public async Task E2E_Authentication_EnforcesApiKeyHeaderAndQuery()
    {
        await using var app = CreateTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        // 1. Without auth -> 401
        using var unauthResp = await client.GetAsync("/api/v1/cameras");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

        // 2. With invalid key -> 401
        using var invalidReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cameras");
        invalidReq.Headers.Add("X-Api-Key", "invalid-key");
        using var invalidResp = await client.SendAsync(invalidReq);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidResp.StatusCode);

        // 3. With valid header -> 200
        using var validReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cameras");
        validReq.Headers.Add("X-Api-Key", ApiKey);
        using var validResp = await client.SendAsync(validReq);
        Assert.Equal(HttpStatusCode.OK, validResp.StatusCode);

        // 4. With valid query param -> 200
        using var queryResp = await client.GetAsync($"/api/v1/cameras?apiKey={ApiKey}");
        Assert.Equal(HttpStatusCode.OK, queryResp.StatusCode);
    }

    [Fact]
    public async Task E2E_CameraProbe_TestsFileSource()
    {
        await using var app = CreateTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

        var probePayload = new
        {
            source = "c:\\Users\\maico\\OneDrive\\Desktop\\Proyectos\\learningAI\\.local-test\\bus.mp4",
            username = (string?)null,
            password = (string?)null
        };

        var response = await client.PostAsJsonAsync("/api/v1/cameras/test", probePayload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.True(body.GetProperty("width").GetInt32() > 0);
        Assert.True(body.GetProperty("height").GetInt32() > 0);
        Assert.True(body.GetProperty("framesPerSecond").GetDouble() > 0);
    }

    [Fact]
    public async Task E2E_AnalyticsCatalog_ReturnsAllFiveAnalytics()
    {
        await using var app = CreateTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

        var response = await client.GetAsync("/api/v1/analytics/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var items = await response.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.NotNull(items);
        var types = items.Select(x => x.GetProperty("id").GetString()).ToList();

        Assert.Contains("hand_raise", types);
        Assert.Contains("person_presence", types);
        Assert.Contains("zone_intrusion", types);
        Assert.Contains("line_crossing", types);
        Assert.Contains("person_counting", types);
    }

    [Fact]
    public async Task E2E_ModelsAndCapacity_ReturnsValidContract()
    {
        await using var app = CreateTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

        // Models (Array of models)
        var modelsResp = await client.GetAsync("/api/v1/models");
        Assert.Equal(HttpStatusCode.OK, modelsResp.StatusCode);
        var modelsJson = await modelsResp.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.NotNull(modelsJson);
        Assert.NotEmpty(modelsJson);

        // Capacity
        var capacityResp = await client.GetAsync("/api/v1/system/capacity");
        Assert.Equal(HttpStatusCode.OK, capacityResp.StatusCode);
        var capacityJson = await capacityResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(capacityJson.TryGetProperty("capacityStatus", out _) || capacityJson.TryGetProperty("capacity_status", out _));
        Assert.True(capacityJson.TryGetProperty("nodeId", out _) || capacityJson.TryGetProperty("node_id", out _));
    }

    [Fact]
    public async Task E2E_FullCameraWorkflow_Camera_Analytics_Zones_Lines_Rules_Alerts_Notifications()
    {
        await using var app = CreateTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

        // 1. Create Camera
        var camId = "cam-e2e-" + Guid.NewGuid().ToString("N")[..6];
        var createCamResp = await client.PostAsJsonAsync("/api/v1/cameras", new
        {
            id = camId,
            name = "E2E Bus Camera",
            source = "c:\\Users\\maico\\OneDrive\\Desktop\\Proyectos\\learningAI\\.local-test\\bus.mp4",
            enabled = true,
            loop = true
        });
        Assert.Equal(HttpStatusCode.Created, createCamResp.StatusCode);

        // 2. Query Camera list
        var listCamResp = await client.GetAsync("/api/v1/cameras");
        Assert.Equal(HttpStatusCode.OK, listCamResp.StatusCode);
        var listJson = await listCamResp.Content.ReadFromJsonAsync<List<JsonElement>>();
        Assert.NotNull(listJson);
        var foundCam = listJson.Any(c => c.GetProperty("id").GetString() == camId);
        Assert.True(foundCam);

        // 3. Add Analytic Instance
        var writeReq = new CameraAnalyticWriteRequest(
            Id: "an-cam1-presence",
            AnalyticTypeId: "person_presence",
            Name: "Presencia de Personas E2E",
            Enabled: true,
            Configuration: new Dictionary<string, object?>
            {
                ["confidence_threshold"] = 0.70,
                ["min_presence_ms"] = 1500,
                ["absence_grace_ms"] = 2000,
                ["emit_snapshot"] = true
            });
        var addAnalyticResp = await client.PostAsJsonAsync($"/api/v1/cameras/{camId}/analytics", writeReq, AnalyticJsonDefaults.Options);
        Assert.Equal(HttpStatusCode.Created, addAnalyticResp.StatusCode);
        var analyticJson = await addAnalyticResp.Content.ReadFromJsonAsync<CameraAnalyticInstance>(AnalyticJsonDefaults.Options);
        Assert.NotNull(analyticJson);
        var instanceId = analyticJson.Id;

        // 4. Update Zones
        var zones = new[]
        {
            new NormalizedZone("Zona E2E", [
                new NormalizedPoint(0.1, 0.1),
                new NormalizedPoint(0.9, 0.1),
                new NormalizedPoint(0.9, 0.9),
                new NormalizedPoint(0.1, 0.9)
            ])
        };
        var updateZoneResp = await client.PutAsJsonAsync($"/api/v1/cameras/{camId}/zones", zones);
        Assert.Equal(HttpStatusCode.OK, updateZoneResp.StatusCode);

        // 5. Add Line
        var validLine = new HandRaise.Domain.Lines.LineDefinition(
            Id: "line-e2e",
            CameraId: camId,
            Name: "Linea E2E",
            PointA: new HandRaise.Domain.Zones.Point2D(0.2, 0.5),
            PointB: new HandRaise.Domain.Zones.Point2D(0.8, 0.5),
            DirectionMode: HandRaise.Domain.Lines.LineDirectionMode.Bidirectional,
            Enabled: true);
        var addLineResp = await client.PostAsJsonAsync($"/api/v1/cameras/{camId}/lines", validLine, AnalyticJsonDefaults.Options);
        Assert.Equal(HttpStatusCode.Created, addLineResp.StatusCode);
        var lineObj = await addLineResp.Content.ReadFromJsonAsync<HandRaise.Domain.Lines.LineDefinition>(AnalyticJsonDefaults.Options);
        Assert.NotNull(lineObj);
        Assert.Equal("line-e2e", lineObj.Id);

        // 6. Add Rule
        var validRule = new AlertRule(
            Id: "rule-e2e-1",
            CameraId: camId,
            AnalyticInstanceId: instanceId,
            Name: "Regla de Prueba E2E",
            Enabled: true,
            Severity: AlertSeverity.High,
            EventTypes: ["person_presence_detected"],
            Conditions: [new RuleCondition("confidence", RuleOperator.GreaterOrEqual, 0.7)],
            CooldownMs: 5000,
            TitleTemplate: "Presencia en {camera_id}",
            DescriptionTemplate: "Persona detectada");

        var addRuleResp = await client.PostAsJsonAsync($"/api/v1/cameras/{camId}/analytics/{instanceId}/rules", validRule, AnalyticJsonDefaults.Options);
        Assert.Equal(HttpStatusCode.Created, addRuleResp.StatusCode);

        // 7. Check Runtime Plan
        var planResp = await client.GetAsync($"/api/v1/cameras/{camId}/runtime-plan");
        Assert.Equal(HttpStatusCode.OK, planResp.StatusCode);
        var planJson = await planResp.Content.ReadFromJsonAsync<CapabilityExecutionPlan>(AnalyticJsonDefaults.Options);
        Assert.NotNull(planJson);
        Assert.Equal(camId, planJson.CameraId);

        // 8. Add Notification Destination
        var validDestination = new NotificationDestination(
            Id: "dest-e2e",
            Name: "Webhook E2E",
            Type: NotificationDestinationType.Webhook,
            Configuration: new Dictionary<string, object?> { ["url"] = "http://localhost:9999/webhook", ["method"] = "POST" },
            Enabled: true);
        var addDestResp = await client.PostAsJsonAsync("/api/v1/notifications/destinations", validDestination, AnalyticJsonDefaults.Options);
        Assert.True(addDestResp.StatusCode == HttpStatusCode.Created, await addDestResp.Content.ReadAsStringAsync());

        // 9. Query Alerts
        var alertsResp = await client.GetAsync("/api/v1/alerts");
        Assert.Equal(HttpStatusCode.OK, alertsResp.StatusCode);

        // 10. Clean up camera
        var deleteCamResp = await client.DeleteAsync($"/api/v1/cameras/{camId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteCamResp.StatusCode);
    }

    [Fact]
    public async Task PhysicalWebcam_DeviceEnumerator_ProbesCorrectly()
    {
        var enumerator = new WindowsVideoDeviceEnumerator();
        var devices = await enumerator.EnumerateDevicesAsync();
        Assert.NotNull(devices);
        Assert.NotEmpty(devices);
    }

    private sealed class TestMemoryCameraStoreAndManager : ICameraManagementService, ICameraStore
    {
        private readonly Dictionary<string, CameraDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);

        // ICameraStore
        public Task InitializeAsync(IReadOnlyList<CameraDefinition> seed, CancellationToken token = default)
        {
            foreach (var s in seed) _definitions[s.Id] = s;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CameraDefinition>> ListAsync(CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<CameraDefinition>>(_definitions.Values.ToList());

        Task<CameraDefinition?> ICameraStore.GetAsync(string id, CancellationToken token) =>
            Task.FromResult(_definitions.GetValueOrDefault(id));

        public Task AddAsync(CameraDefinition camera, CancellationToken token = default)
        {
            _definitions[camera.Id] = camera;
            return Task.CompletedTask;
        }

        public Task<bool> UpdateAsync(CameraDefinition camera, CancellationToken token = default)
        {
            if (!_definitions.ContainsKey(camera.Id)) return Task.FromResult(false);
            _definitions[camera.Id] = camera;
            return Task.FromResult(true);
        }

        Task<bool> ICameraStore.DeleteAsync(string id, CancellationToken token) =>
            Task.FromResult(_definitions.Remove(id));

        // ICameraManagementService
        public event Action<string, bool>? CameraRunningStateChanged;
        public event Action? CamerasChanged;

        async Task<IReadOnlyList<CameraView>> ICameraManagementService.ListAsync(CancellationToken token)
        {
            var defs = await ListAsync(token);
            return defs.Select(d => new CameraView(d.Id, d.Name, d.Source, d.Enabled, false, true, 30.0, null)).ToList();
        }

        Task<CameraView?> ICameraManagementService.GetAsync(string id, CancellationToken token)
        {
            if (!_definitions.TryGetValue(id, out var d)) return Task.FromResult<CameraView?>(null);
            return Task.FromResult<CameraView?>(new CameraView(d.Id, d.Name, d.Source, d.Enabled, false, true, 30.0, null));
        }

        public Task<CameraView> CreateAsync(CameraWriteRequest request, CancellationToken token = default)
        {
            var def = new CameraDefinition(request.Id, request.Name, request.Source, request.Enabled, request.Loop, []);
            _definitions[request.Id] = def;
            return Task.FromResult(new CameraView(def.Id, def.Name, def.Source, def.Enabled, false, true, 30.0, null));
        }

        public Task<CameraView?> UpdateAsync(string id, CameraWriteRequest request, CancellationToken token = default)
        {
            if (!_definitions.TryGetValue(id, out var existing)) return Task.FromResult<CameraView?>(null);
            var updated = existing with { Name = request.Name, Source = request.Source, Enabled = request.Enabled, Loop = request.Loop };
            _definitions[id] = updated;
            return Task.FromResult<CameraView?>(new CameraView(updated.Id, updated.Name, updated.Source, updated.Enabled, false, true, 30.0, null));
        }

        Task<bool> ICameraManagementService.DeleteAsync(string id, CancellationToken token) =>
            Task.FromResult(_definitions.Remove(id));

        public Task<CameraView?> StartAsync(string id, CancellationToken token = default)
        {
            if (!_definitions.TryGetValue(id, out var d)) return Task.FromResult<CameraView?>(null);
            return Task.FromResult<CameraView?>(new CameraView(d.Id, d.Name, d.Source, d.Enabled, true, true, 30.0, null));
        }

        public Task<CameraView?> StopAsync(string id, CancellationToken token = default)
        {
            if (!_definitions.TryGetValue(id, out var d)) return Task.FromResult<CameraView?>(null);
            return Task.FromResult<CameraView?>(new CameraView(d.Id, d.Name, d.Source, d.Enabled, false, true, 30.0, null));
        }

        public Task<CameraTestResult> TestAsync(CameraTestRequest request, CancellationToken token = default) =>
            Task.FromResult(new CameraTestResult(true, null, 1920, 1080, 30.0, 150.0, [0xFF, 0xD8, 0xFF, 0xD9]));

        public Task<byte[]?> GetSnapshotAsync(string id, CancellationToken token = default) =>
            Task.FromResult<byte[]?>([0xFF, 0xD8, 0xFF, 0xD9]);

        public Task<IReadOnlyList<NormalizedZone>?> GetZonesAsync(string id, CancellationToken token = default) =>
            Task.FromResult<IReadOnlyList<NormalizedZone>?>([]);

        public Task<bool> UpdateZonesAsync(string id, IReadOnlyList<NormalizedZone> zones, CancellationToken token = default) =>
            Task.FromResult(true);

        public Task<DeviceChangeResult> ChangeDeviceAsync(string deviceId, CancellationToken token = default) =>
            Task.FromResult(new DeviceChangeResult(true, deviceId, null, 10.0));

        public Task<CameraStreamSubscriptionResult> SubscribeStreamAsync(string id, double fps, int quality, CancellationToken token = default) =>
            Task.FromResult(new CameraStreamSubscriptionResult(CameraStreamStatus.Offline));
    }
}
