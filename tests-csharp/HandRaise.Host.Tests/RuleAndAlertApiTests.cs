using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HandRaise.Application.Analytics;
using HandRaise.Application.Rules;
using HandRaise.Application.Storage;
using HandRaise.Application.Zones;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Rules;
using HandRaise.Domain.Zones;
using HandRaise.Host.Services;
using HandRaise.Infrastructure.Windows.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HandRaise.Host.Tests;

public sealed class RuleAndAlertApiTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _rulesStorePath;
    private readonly string _alertsStorePath;

    public RuleAndAlertApiTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"rule_alert_api_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _rulesStorePath = Path.Combine(_tempDirectory, "rules.json");
        _alertsStorePath = Path.Combine(_tempDirectory, "alerts.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task RulesCrudEndToEnd()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        // 1. Initial list should be empty
        var initial = await client.GetFromJsonAsync<List<AlertRule>>(
            "/api/v1/cameras/cam-1/analytics/inst-1/rules", AnalyticJsonDefaults.Options);
        Assert.NotNull(initial);
        Assert.Empty(initial);

        // 2. Create invalid rule -> 400
        var invalidRule = new AlertRule(
            Id: "",
            CameraId: "cam-1",
            AnalyticInstanceId: "inst-1",
            Name: "",
            Enabled: true,
            Severity: AlertSeverity.High,
            EventTypes: []);
        var invalidResp = await client.PostAsJsonAsync(
            "/api/v1/cameras/cam-1/analytics/inst-1/rules", invalidRule, AnalyticJsonDefaults.Options);
        Assert.Equal(HttpStatusCode.BadRequest, invalidResp.StatusCode);

        // 3. Create valid rule -> 201
        var validRule = new AlertRule(
            Id: "rule-aforo",
            CameraId: "cam-1",
            AnalyticInstanceId: "inst-1",
            Name: "Alerta de Aforo Alto",
            Enabled: true,
            Severity: AlertSeverity.High,
            EventTypes: ["person_count_updated"],
            Conditions: [new RuleCondition("current_occupancy", RuleOperator.GreaterOrEqual, 50)],
            CooldownMs: 5000,
            TitleTemplate: "Aforo alto en {camera_id}",
            DescriptionTemplate: "Ocupación: {metadata.current_occupancy}");

        var createResp = await client.PostAsJsonAsync(
            "/api/v1/cameras/cam-1/analytics/inst-1/rules", validRule, AnalyticJsonDefaults.Options);
        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<AlertRule>(AnalyticJsonDefaults.Options);
        Assert.NotNull(created);
        Assert.Equal("rule-aforo", created!.Id);
        Assert.Equal("Alerta de Aforo Alto", created.Name);

        // 4. Get by Id -> 200
        var getResp = await client.GetFromJsonAsync<AlertRule>(
            "/api/v1/cameras/cam-1/analytics/inst-1/rules/rule-aforo", AnalyticJsonDefaults.Options);
        Assert.NotNull(getResp);
        Assert.Equal("rule-aforo", getResp!.Id);

        // 5. Update rule -> 200
        var updatedRule = validRule with { Name = "Alerta de Aforo Extremo", Severity = AlertSeverity.Critical };
        var putResp = await client.PutAsJsonAsync(
            "/api/v1/cameras/cam-1/analytics/inst-1/rules/rule-aforo", updatedRule, AnalyticJsonDefaults.Options);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        var updated = await putResp.Content.ReadFromJsonAsync<AlertRule>(AnalyticJsonDefaults.Options);
        Assert.NotNull(updated);
        Assert.Equal("Alerta de Aforo Extremo", updated!.Name);
        Assert.Equal(AlertSeverity.Critical, updated.Severity);

        // 6. Delete rule -> 204
        var delResp = await client.DeleteAsync("/api/v1/cameras/cam-1/analytics/inst-1/rules/rule-aforo");
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        // 7. Get deleted -> 404
        var getDeleted = await client.GetAsync("/api/v1/cameras/cam-1/analytics/inst-1/rules/rule-aforo");
        Assert.Equal(HttpStatusCode.NotFound, getDeleted.StatusCode);
    }

    [Fact]
    public async Task AlertsQueryAndLifecycleEndToEnd()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var alertStore = app.Services.GetRequiredService<IAlertStore>();

        var alert1 = new OperationalAlert(
            Id: "alt-test-01",
            RuleId: "rule-1",
            CameraId: "cam-1",
            AnalyticInstanceId: "inst-1",
            SourceEventId: "ev-1",
            Severity: AlertSeverity.High,
            Status: AlertStatus.Open,
            Title: "Alerta 1",
            Description: "Intrusión detectada",
            CreatedAt: DateTimeOffset.UtcNow);

        await alertStore.SaveAsync(alert1);

        // 1. Query alerts -> 200
        var alerts = await client.GetFromJsonAsync<List<OperationalAlert>>("/api/v1/alerts", AnalyticJsonDefaults.Options);
        Assert.NotNull(alerts);
        Assert.Single(alerts);
        Assert.Equal("alt-test-01", alerts[0].Id);
        Assert.Equal(AlertStatus.Open, alerts[0].Status);

        // 2. Filter by status
        var openAlerts = await client.GetFromJsonAsync<List<OperationalAlert>>("/api/v1/alerts?status=open", AnalyticJsonDefaults.Options);
        Assert.Single(openAlerts!);
        var resolvedAlerts = await client.GetFromJsonAsync<List<OperationalAlert>>("/api/v1/alerts?status=resolved", AnalyticJsonDefaults.Options);
        Assert.Empty(resolvedAlerts!);

        // 3. Acknowledge alert -> 200
        var ackResp = await client.PostAsync("/api/v1/alerts/alt-test-01/acknowledge", null);
        Assert.Equal(HttpStatusCode.OK, ackResp.StatusCode);
        var acked = await ackResp.Content.ReadFromJsonAsync<OperationalAlert>(AnalyticJsonDefaults.Options);
        Assert.NotNull(acked);
        Assert.Equal(AlertStatus.Acknowledged, acked!.Status);
        Assert.NotNull(acked.AcknowledgedAt);

        // 4. Resolve alert -> 200
        var resolveResp = await client.PostAsync("/api/v1/alerts/alt-test-01/resolve", null);
        Assert.Equal(HttpStatusCode.OK, resolveResp.StatusCode);
        var resolved = await resolveResp.Content.ReadFromJsonAsync<OperationalAlert>(AnalyticJsonDefaults.Options);
        Assert.NotNull(resolved);
        Assert.Equal(AlertStatus.Resolved, resolved!.Status);
        Assert.NotNull(resolved.ResolvedAt);
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
                ["storage:rulesStorePath"] = _rulesStorePath,
                ["storage:alertsStorePath"] = _alertsStorePath
            });
            builder.Services.AddSingleton<ICameraStore>(new FakeCameraStore());
            builder.Services.AddSingleton<IHandEventRepository>(new ReadApiTests.FakeRepositoryForD2());
        });

    private sealed class FakeCameraStore : ICameraStore
    {
        private readonly Dictionary<string, CameraDefinition> _cameras = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cam-1"] = new CameraDefinition("cam-1", "Cam 1", "0", true, false, [])
        };

        public Task InitializeAsync(IReadOnlyList<CameraDefinition> seed, CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CameraDefinition>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<CameraDefinition>>(_cameras.Values.ToArray());
        public Task<CameraDefinition?> GetAsync(string id, CancellationToken token = default) => Task.FromResult(_cameras.GetValueOrDefault(id));
        public Task AddAsync(CameraDefinition camera, CancellationToken token = default) { _cameras[camera.Id] = camera; return Task.CompletedTask; }
        public Task<bool> UpdateAsync(CameraDefinition camera, CancellationToken token = default) { _cameras[camera.Id] = camera; return Task.FromResult(true); }
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) => Task.FromResult(_cameras.Remove(id));
    }
}
