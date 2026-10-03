using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HandRaise.Application.Analytics;
using HandRaise.Application.Lines;
using HandRaise.Application.Storage;
using HandRaise.Application.Zones;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Lines;
using HandRaise.Domain.Zones;
using HandRaise.Host.Services;
using HandRaise.Infrastructure.Windows.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HandRaise.Host.Tests;

public sealed class LineManagementApiTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _analyticStorePath;
    private readonly string _linesStorePath;

    public LineManagementApiTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"line_api_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _analyticStorePath = Path.Combine(_tempDirectory, "analytics.json");
        _linesStorePath = Path.Combine(_tempDirectory, "lines.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task LinesCrudEndToEnd()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        // 1. Initial list should be empty
        var initialList = await client.GetFromJsonAsync<List<LineDefinition>>("/api/v1/cameras/cam-1/lines", AnalyticJsonDefaults.Options);
        Assert.NotNull(initialList);
        Assert.Empty(initialList);

        // 2. Non-existent camera returns 404
        var notFound = await client.GetAsync("/api/v1/cameras/non-existent/lines");
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

        // 3. Create invalid line returns 400
        var invalidLine = new LineDefinition(
            Id: "line-invalid",
            CameraId: "cam-1",
            Name: "Invalid Line",
            PointA: new Point2D(-0.5, 0.2),
            PointB: new Point2D(0.8, 0.2));
        var invalidCreate = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/lines", invalidLine, AnalyticJsonDefaults.Options);
        Assert.Equal(HttpStatusCode.BadRequest, invalidCreate.StatusCode);

        // 4. Create valid line returns 201 Created
        var validLine = new LineDefinition(
            Id: "line-entry",
            CameraId: "cam-1",
            Name: "Entry Tripwire",
            PointA: new Point2D(0.1, 0.5),
            PointB: new Point2D(0.9, 0.5),
            DirectionMode: LineDirectionMode.AToB,
            Enabled: true);
        var createResponse = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/lines", validLine, AnalyticJsonDefaults.Options);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<LineDefinition>(AnalyticJsonDefaults.Options);
        Assert.NotNull(created);
        Assert.Equal("line-entry", created.Id);
        Assert.Equal("Entry Tripwire", created.Name);

        // 5. List returns created line
        var listAfterCreate = await client.GetFromJsonAsync<List<LineDefinition>>("/api/v1/cameras/cam-1/lines", AnalyticJsonDefaults.Options);
        Assert.NotNull(listAfterCreate);
        Assert.Single(listAfterCreate);
        Assert.Equal("line-entry", listAfterCreate[0].Id);

        // 6. Update line
        var updatedLine = validLine with { Name = "Updated Tripwire", DirectionMode = LineDirectionMode.Bidirectional };
        var updateResp = await client.PutAsJsonAsync("/api/v1/cameras/cam-1/lines/line-entry", updatedLine, AnalyticJsonDefaults.Options);
        Assert.Equal(HttpStatusCode.OK, updateResp.StatusCode);
        var updated = await updateResp.Content.ReadFromJsonAsync<LineDefinition>(AnalyticJsonDefaults.Options);
        Assert.NotNull(updated);
        Assert.Equal("Updated Tripwire", updated.Name);
        Assert.Equal(LineDirectionMode.Bidirectional, updated.DirectionMode);

        // 7. Bulk update lines
        var secondLine = new LineDefinition(
            Id: "line-exit",
            CameraId: "cam-1",
            Name: "Exit Tripwire",
            PointA: new Point2D(0.2, 0.8),
            PointB: new Point2D(0.8, 0.8),
            DirectionMode: LineDirectionMode.BToA,
            Enabled: true);
        var bulkResp = await client.PutAsJsonAsync("/api/v1/cameras/cam-1/lines", new[] { updatedLine, secondLine }, AnalyticJsonDefaults.Options);
        Assert.Equal(HttpStatusCode.OK, bulkResp.StatusCode);

        var listAfterBulk = await client.GetFromJsonAsync<List<LineDefinition>>("/api/v1/cameras/cam-1/lines", AnalyticJsonDefaults.Options);
        Assert.NotNull(listAfterBulk);
        Assert.Equal(2, listAfterBulk.Count);

        // 8. Delete line
        var deleteResp = await client.DeleteAsync("/api/v1/cameras/cam-1/lines/line-exit");
        Assert.Equal(HttpStatusCode.NoContent, deleteResp.StatusCode);

        var listAfterDelete = await client.GetFromJsonAsync<List<LineDefinition>>("/api/v1/cameras/cam-1/lines", AnalyticJsonDefaults.Options);
        Assert.NotNull(listAfterDelete);
        Assert.Single(listAfterDelete);
        Assert.Equal("line-entry", listAfterDelete[0].Id);
    }

    [Fact]
    public async Task AnalyticCreation_RequiresValidAssignedLines()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        // 1. Create a line first
        var validLine = new LineDefinition(
            Id: "line-main",
            CameraId: "cam-1",
            Name: "Main Entrance",
            PointA: new Point2D(0.0, 0.5),
            PointB: new Point2D(1.0, 0.5));
        await client.PostAsJsonAsync("/api/v1/cameras/cam-1/lines", validLine, AnalyticJsonDefaults.Options);

        // 2. Line Crossing without assigned_line_ids returns 400
        var noLinesResp = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            Id: "an-lc-invalid",
            AnalyticTypeId: "line_crossing",
            Name: "Line Crossing No Lines",
            AssignedLineIds: []));
        Assert.Equal(HttpStatusCode.BadRequest, noLinesResp.StatusCode);
        var error1 = await noLinesResp.Content.ReadAsStringAsync();
        Assert.Contains("requiere al menos una línea", error1, StringComparison.OrdinalIgnoreCase);

        // 3. Line Crossing with non-existent line returns 400
        var nonExistentLineResp = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            Id: "an-lc-ghost",
            AnalyticTypeId: "line_crossing",
            Name: "Line Crossing Ghost Line",
            AssignedLineIds: ["line-ghost-xyz"]));
        Assert.Equal(HttpStatusCode.BadRequest, nonExistentLineResp.StatusCode);
        var error2 = await nonExistentLineResp.Content.ReadAsStringAsync();
        Assert.Contains("no existe", error2, StringComparison.OrdinalIgnoreCase);

        // 4. Line Crossing with valid line succeeds (201 Created)
        var validLcResp = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            Id: "an-lc-valid",
            AnalyticTypeId: "line_crossing",
            Name: "Line Crossing Valid",
            AssignedLineIds: ["line-main"]));
        Assert.Equal(HttpStatusCode.Created, validLcResp.StatusCode);

        // 5. Person Counting with valid line succeeds (201 Created)
        var validPcResp = await client.PostAsJsonAsync("/api/v1/cameras/cam-1/analytics", new CameraAnalyticWriteRequest(
            Id: "an-pc-valid",
            AnalyticTypeId: "person_counting",
            Name: "Person Counting Valid",
            Configuration: new Dictionary<string, object?>
            {
                ["initial_occupancy"] = 0,
                ["maximum_occupancy"] = 100
            },
            AssignedLineIds: ["line-main"]));
        Assert.Equal(HttpStatusCode.Created, validPcResp.StatusCode);
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
                ["storage:analyticStorePath"] = _analyticStorePath,
                ["storage:linesStorePath"] = _linesStorePath
            });
            builder.Services.AddSingleton<ICameraStore>(new FakeCameraStore());
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
}

