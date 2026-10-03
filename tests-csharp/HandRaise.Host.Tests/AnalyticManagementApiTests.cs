using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HandRaise.Application.Analytics;
using HandRaise.Application.Storage;
using HandRaise.Application.Zones;
using HandRaise.Domain.Analytics;
using HandRaise.Host.Services;
using HandRaise.Infrastructure.Windows.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HandRaise.Host.Tests;

public sealed class AnalyticManagementApiTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _analyticStorePath;

    public AnalyticManagementApiTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"analytic_api_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _analyticStorePath = Path.Combine(_tempDirectory, "analytics.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task GetCatalogReturnsAllStandardAnalytics()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var response = await client.GetAsync("/api/v1/analytics/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        var catalog = JsonSerializer.Deserialize<List<AnalyticDefinition>>(content, AnalyticJsonDefaults.Options);
        Assert.NotNull(catalog);
        Assert.NotEmpty(catalog);
        Assert.Contains(catalog, item => item.Id == "hand_raise");
        Assert.Contains(catalog, item => item.Id == "person_presence");
        Assert.Contains(catalog, item => item.Id == "zone_intrusion");
    }

    [Fact]
    public async Task AnalyticsCrudEndToEnd()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        // 1. Initial list should be empty
        var initialList = await client.GetFromJsonAsync<List<CameraAnalyticInstance>>("/api/v1/cameras/cam-1/analytics", AnalyticJsonDefaults.Options);
        Assert.NotNull(initialList);
        Assert.Empty(initialList);

        // 2. Missing camera returns 404
        var missingCam = await client.GetAsync("/api/v1/cameras/non-existent/analytics");
        Assert.Equal(HttpStatusCode.NotFound, missingCam.StatusCode);

        // 3. Create analytic with invalid param returns 400
        var invalidCreate = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            Id: "an-1",
            AnalyticTypeId: "hand_raise",
            Name: "Invalid Params",
            Configuration: new Dictionary<string, object?> { ["consecutive_frames"] = 99 }));
        Assert.Equal(HttpStatusCode.BadRequest, invalidCreate.StatusCode);

        // 4. Create valid analytic returns 201 Created
        var validCreate = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            Id: "an-cam1-hr",
            AnalyticTypeId: "hand_raise",
            Name: "Detección Manos Recepción",
            Enabled: true,
            Configuration: new Dictionary<string, object?>
            {
                ["strict_mode"] = true,
                ["consecutive_frames"] = 4,
                ["cooldown_ms"] = 1200
            },
            AssignedZoneIds: ["zone-1"]));
        Assert.Equal(HttpStatusCode.Created, validCreate.StatusCode);
        Assert.NotNull(validCreate.Headers.Location);

        var createdInstance = await validCreate.Content.ReadFromJsonAsync<CameraAnalyticInstance>(AnalyticJsonDefaults.Options);
        Assert.NotNull(createdInstance);
        Assert.Equal("an-cam1-hr", createdInstance.Id);
        Assert.Equal("cam-1", createdInstance.CameraId);
        Assert.Equal(AnalyticStatus.Active, createdInstance.Status);

        // 5. Get by ID
        var getResponse = await client.GetAsync("/api/v1/cameras/cam-1/analytics/an-cam1-hr");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetchedInstance = await getResponse.Content.ReadFromJsonAsync<CameraAnalyticInstance>(AnalyticJsonDefaults.Options);
        Assert.NotNull(fetchedInstance);
        Assert.Equal("Detección Manos Recepción", fetchedInstance.Name);

        // 6. Update
        var updateResponse = await client.PutAsJsonAsync("/api/v1/cameras/cam-1/analytics/an-cam1-hr", new CameraAnalyticWriteRequest(
            Name: "Manos Actualizado",
            Enabled: false,
            Configuration: new Dictionary<string, object?>
            {
                ["strict_mode"] = false,
                ["consecutive_frames"] = 5,
                ["cooldown_ms"] = 2000
            }));
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updatedInstance = await updateResponse.Content.ReadFromJsonAsync<CameraAnalyticInstance>(AnalyticJsonDefaults.Options);
        Assert.NotNull(updatedInstance);
        Assert.Equal("Manos Actualizado", updatedInstance.Name);
        Assert.False(updatedInstance.Enabled);
        Assert.Equal(AnalyticStatus.Inactive, updatedInstance.Status);

        // 7. Delete
        var deleteResponse = await client.DeleteAsync("/api/v1/cameras/cam-1/analytics/an-cam1-hr");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // 8. Get after delete returns 404
        var getAfterDelete = await client.GetAsync("/api/v1/cameras/cam-1/analytics/an-cam1-hr");
        Assert.Equal(HttpStatusCode.NotFound, getAfterDelete.StatusCode);
    }

    [Fact]
    public async Task PersonPresenceCrudAndCoexistenceEndToEnd()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        // 1. Create hand_raise
        var createHr = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            Id: "an-cam1-hr",
            AnalyticTypeId: "hand_raise",
            Name: "Detección Manos"));
        Assert.Equal(HttpStatusCode.Created, createHr.StatusCode);

        // 2. Create person_presence
        var createPresence = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            Id: "an-cam1-presence",
            AnalyticTypeId: "person_presence",
            Name: "Presencia Entrada",
            Configuration: new Dictionary<string, object?>
            {
                ["confidence_threshold"] = 0.70,
                ["min_presence_ms"] = 1500,
                ["absence_grace_ms"] = 2000,
                ["emit_snapshot"] = true
            },
            AssignedZoneIds: ["zone-entrada"]));
        Assert.Equal(HttpStatusCode.Created, createPresence.StatusCode);

        // 3. List should return both instances
        var list = await client.GetFromJsonAsync<List<CameraAnalyticInstance>>("/api/v1/cameras/cam-1/analytics", AnalyticJsonDefaults.Options);
        Assert.NotNull(list);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, x => x.AnalyticTypeId == "hand_raise");
        Assert.Contains(list, x => x.AnalyticTypeId == "person_presence");

        // 4. Hot update person_presence
        var updatePresence = await client.PutAsJsonAsync("/api/v1/cameras/cam-1/analytics/an-cam1-presence", new CameraAnalyticWriteRequest(
            Name: "Presencia Entrada Modificada",
            Enabled: false,
            Configuration: new Dictionary<string, object?>
            {
                ["confidence_threshold"] = 0.80,
                ["min_presence_ms"] = 3000
            }));
        Assert.Equal(HttpStatusCode.OK, updatePresence.StatusCode);
        var updated = await updatePresence.Content.ReadFromJsonAsync<CameraAnalyticInstance>(AnalyticJsonDefaults.Options);
        Assert.NotNull(updated);
        Assert.Equal("Presencia Entrada Modificada", updated.Name);
        Assert.False(updated.Enabled);
        Assert.Equal(AnalyticStatus.Inactive, updated.Status);

        // 5. Delete person_presence leaves hand_raise intact
        var deletePresence = await client.DeleteAsync("/api/v1/cameras/cam-1/analytics/an-cam1-presence");
        Assert.Equal(HttpStatusCode.NoContent, deletePresence.StatusCode);

        var remainingList = await client.GetFromJsonAsync<List<CameraAnalyticInstance>>("/api/v1/cameras/cam-1/analytics", AnalyticJsonDefaults.Options);
        Assert.NotNull(remainingList);
        Assert.Single(remainingList);
        Assert.Equal("an-cam1-hr", remainingList[0].Id);
    }

    [Fact]
    public async Task ZoneIntrusionCreationRejectsWithoutAssignedZones()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var response = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            Id: "an-cam1-intrusion-nozone",
            AnalyticTypeId: "zone_intrusion",
            Name: "Intrusión Inválida",
            AssignedZoneIds: []));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("requiere al menos una zona", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ZoneIntrusionCreationRejectsNonExistentZone()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var response = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            Id: "an-cam1-intrusion-wrongzone",
            AnalyticTypeId: "zone_intrusion",
            Name: "Intrusión Zona Fantasma",
            AssignedZoneIds: ["non-existent-zone-xyz"]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("no existe", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ZoneIntrusionCrudAndTriCoexistenceEndToEnd()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        // 1. Create HandRaise
        var createHr = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            Id: "an-cam1-hr",
            AnalyticTypeId: "hand_raise",
            Name: "Mano Principal"));
        Assert.Equal(HttpStatusCode.Created, createHr.StatusCode);

        // 2. Create PersonPresence
        var createPresence = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            Id: "an-cam1-presence",
            AnalyticTypeId: "person_presence",
            Name: "Presencia Entrada",
            AssignedZoneIds: ["zone-entrada"]));
        Assert.Equal(HttpStatusCode.Created, createPresence.StatusCode);

        // 3. Create ZoneIntrusion (Area A)
        var createIntrusionA = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            Id: "an-cam1-intrusion-vault",
            AnalyticTypeId: "zone_intrusion",
            Name: "Intrusión Vault",
            Configuration: new Dictionary<string, object?>
            {
                ["confidence_threshold"] = 0.65,
                ["entry_delay_ms"] = 500,
                ["exit_grace_ms"] = 1000,
                ["emit_snapshot"] = true
            },
            AssignedZoneIds: ["zone-vault"]));
        Assert.Equal(HttpStatusCode.Created, createIntrusionA.StatusCode);

        // 4. Create ZoneIntrusion (Area B)
        var createIntrusionB = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            Id: "an-cam1-intrusion-entrada",
            AnalyticTypeId: "zone_intrusion",
            Name: "Intrusión Entrada",
            Configuration: new Dictionary<string, object?>
            {
                ["confidence_threshold"] = 0.60,
                ["entry_delay_ms"] = 1000
            },
            AssignedZoneIds: ["zone-entrada"]));
        Assert.Equal(HttpStatusCode.Created, createIntrusionB.StatusCode);

        // 5. List all 4 instances
        var list = await client.GetFromJsonAsync<List<CameraAnalyticInstance>>("/api/v1/cameras/cam-1/analytics", AnalyticJsonDefaults.Options);
        Assert.NotNull(list);
        Assert.Equal(4, list.Count);
        Assert.Contains(list, x => x.Id == "an-cam1-hr");
        Assert.Contains(list, x => x.Id == "an-cam1-presence");
        Assert.Contains(list, x => x.Id == "an-cam1-intrusion-vault");
        Assert.Contains(list, x => x.Id == "an-cam1-intrusion-entrada");

        // 6. Hot update ZoneIntrusion
        var updateResp = await client.PutAsJsonAsync("/api/v1/cameras/cam-1/analytics/an-cam1-intrusion-vault", new CameraAnalyticWriteRequest(
            Name: "Intrusión Vault Actualizada",
            Enabled: false,
            Configuration: new Dictionary<string, object?>
            {
                ["confidence_threshold"] = 0.80,
                ["entry_delay_ms"] = 2000
            }));
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updated = await updateResp.Content.ReadFromJsonAsync<CameraAnalyticInstance>(AnalyticJsonDefaults.Options);
        Assert.NotNull(updated);
        Assert.Equal("Intrusión Vault Actualizada", updated.Name);
        Assert.False(updated.Enabled);
        Assert.Equal(AnalyticStatus.Inactive, updated.Status);

        // 7. Verify persistence across store load
        var diskStore = new JsonAnalyticInstanceStore(_analyticStorePath);
        var persisted = await diskStore.GetByIdAsync("an-cam1-intrusion-vault");
        Assert.NotNull(persisted);
        Assert.Equal("Intrusión Vault Actualizada", persisted.Name);
        Assert.False(persisted.Enabled);

        // 8. Hot delete ZoneIntrusion
        var deleteResp = await client.DeleteAsync("/api/v1/cameras/cam-1/analytics/an-cam1-intrusion-vault");
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        var remaining = await client.GetFromJsonAsync<List<CameraAnalyticInstance>>("/api/v1/cameras/cam-1/analytics", AnalyticJsonDefaults.Options);
        Assert.NotNull(remaining);
        Assert.Equal(3, remaining.Count);
        Assert.DoesNotContain(remaining, x => x.Id == "an-cam1-intrusion-vault");
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
            builder.Services.AddSingleton<IHandEventRepository>(new ReadApiTests.FakeRepositoryForD2());
        });

    private sealed class FakeCameraStore : ICameraStore
    {
        private readonly Dictionary<string, CameraDefinition> _cameras = new(StringComparer.OrdinalIgnoreCase)
        {
            ["cam-1"] = new CameraDefinition("cam-1", "Cam 1", "0", true, false, [
                new NormalizedZone("zone-vault", [new(0, 0), new(0.5, 0), new(0.5, 0.5), new(0, 0.5)]),
                new NormalizedZone("zone-entrada", [new(0.5, 0.5), new(1, 0.5), new(1, 1), new(0.5, 1)])
            ])
        };

        public Task InitializeAsync(IReadOnlyList<CameraDefinition> seed, CancellationToken token = default) => Task.CompletedTask;
        public Task<IReadOnlyList<CameraDefinition>> ListAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<CameraDefinition>>(_cameras.Values.ToArray());
        public Task<CameraDefinition?> GetAsync(string id, CancellationToken token = default) => Task.FromResult(_cameras.GetValueOrDefault(id));
        public Task AddAsync(CameraDefinition camera, CancellationToken token = default) { _cameras[camera.Id] = camera; return Task.CompletedTask; }
        public Task<bool> UpdateAsync(CameraDefinition camera, CancellationToken token = default) { _cameras[camera.Id] = camera; return Task.FromResult(true); }
        public Task<bool> DeleteAsync(string id, CancellationToken token = default) => Task.FromResult(_cameras.Remove(id));
    }
}
