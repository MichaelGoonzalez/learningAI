using System.Net;
using System.Net.Http.Json;
using HandRaise.Application.Events;
using HandRaise.Application.Storage;
using HandRaise.Host;
using HandRaise.Host.Services;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using HandRaise.Application.Pipelines;
using System.Text.Json;

namespace HandRaise.Host.Tests;

public sealed class ReadApiTests
{
    [Fact]
    public async Task ApiKeyRejectsMissingKeyAndAcceptsConfiguredKey()
    {
        await using var app = CreateApp(new FakeRepository(), "secret");
        await app.StartAsync();
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/health")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");
        var authorized = await client.GetAsync("/api/v1/health");
        Assert.Equal(HttpStatusCode.OK, authorized.StatusCode);
        using var health = JsonDocument.Parse(await authorized.Content.ReadAsStringAsync());
        Assert.Equal("node-test", health.RootElement.GetProperty("node_id").GetString());
        Assert.Equal("site-test", health.RootElement.GetProperty("site_id").GetString());
    }

    [Fact]
    public async Task EventsApplyFiltersAndPagination()
    {
        var events = new[]
        {
            Event("1", "cam-a", "hand_raised", 1),
            Event("2", "cam-a", "hand_raised", 2),
            Event("3", "cam-b", "hand_lowered", 3)
        };
        var repository = new FakeRepository(events);
        await using var app = CreateApp(repository, "secret");
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var page = await client.GetFromJsonAsync<PagedEvents>(
            "/api/v1/events?camera=cam-a&type=hand_raised&limit=1&offset=1");

        Assert.NotNull(page);
        Assert.Single(page!.Items);
        Assert.Equal("2", page.Items[0].Id);
        Assert.Equal("node-test", page.Items[0].NodeId);
        Assert.Equal("site-test", page.Items[0].SiteId);
        Assert.Equal("cam-a", repository.LastQuery!.CameraId);
        Assert.Equal(1, repository.LastQuery.Offset);
    }

    [Fact]
    public async Task EventsRejectInvalidPaginationWithProblemDetails()
    {
        await using var app = CreateApp(new FakeRepository(), "secret");
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var response = await client.GetAsync("/api/v1/events?limit=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task MissingSnapshotReturns404()
    {
        await using var app = CreateApp(new FakeRepository(), "secret");
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync("/api/v1/events/missing/snapshot")).StatusCode);
    }

    [Fact]
    public async Task CapacityUsesAverageDecodeAndInferenceMetrics()
    {
        await using var app = CreateApp(new FakeRepository(), "secret");
        await app.StartAsync();
        var metrics = app.Services.GetRequiredService<PipelineMetricsRegistry>();
        metrics.Register("cam-a", "Camera A");
        metrics.Update("cam-a", new CameraPipelineMetrics(
            1, 0, 20, 10, 0, 2, 40, 1, 1, 54));
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        using var result = JsonDocument.Parse(
            await client.GetStringAsync("/api/v1/system/capacity"));

        Assert.Equal(5, result.RootElement.GetProperty("targetFramesPerSecond").GetDouble());
        Assert.Equal(4, result.RootElement.GetProperty("estimatedMaximumCameras").GetInt32());
    }

    private static WebApplication CreateApp(FakeRepository repository, string apiKey) =>
        HostApplication.Build([], startCameras: false, builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Environment.EnvironmentName = "Production";
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["api:apiKey"] = apiKey,
                ["api:apiKeyHeader"] = "X-Api-Key",
                ["nodeId"] = "node-test",
                ["siteId"] = "site-test",
                ["capacity:targetFramesPerSecond"] = "5",
                ["storage:snapshotDirectory"] = Path.GetTempPath()
            });
            builder.Services.AddSingleton<IHandEventRepository>(repository);
        });

    private static HandEvent Event(string id, string camera, string type, int minute) => new(
        id, type, camera, 1, "left", "zone", 0.9,
        new DateTimeOffset(2026, 1, 1, 0, minute, 0, TimeSpan.Zero),
        NodeId: "node-test",
        SiteId: "site-test");

    internal class FakeRepositoryForD2(IEnumerable<HandEvent>? events = null) : IHandEventRepository
    {
        private readonly HandEvent[] _events = events?.ToArray() ?? [];
        public EventQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<HandEvent>> QueryAsync(EventQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            query.Validate();
            var result = _events.AsEnumerable();
            if (query.CameraId is not null) result = result.Where(item => item.CameraId == query.CameraId);
            if (query.Type is not null) result = result.Where(item => item.Type == query.Type);
            if (query.Zone is not null) result = result.Where(item => item.Zone == query.Zone);
            if (query.From is not null) result = result.Where(item => item.Timestamp >= query.From);
            if (query.To is not null) result = result.Where(item => item.Timestamp <= query.To);
            return Task.FromResult<IReadOnlyList<HandEvent>>(result.Skip(query.Offset).Take(query.Limit).ToArray());
        }

        public Task<HandEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_events.FirstOrDefault(item => item.Id == id));
        public Task<IReadOnlyList<EventStatistic>> GetStatisticsAsync(EventQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EventStatistic>>([]);
        public Task SaveBatchAsync(IReadOnlyList<HandEvent> events, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<StoredSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StoredSnapshot>>([]);
        public Task DeleteEventsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearSnapshotPathAsync(string eventId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeRepository(IEnumerable<HandEvent>? events = null) : FakeRepositoryForD2(events);
}
