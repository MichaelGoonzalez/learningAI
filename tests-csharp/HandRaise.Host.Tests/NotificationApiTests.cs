using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HandRaise.Application.Notifications;
using HandRaise.Application.Storage;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Notifications;
using HandRaise.Domain.Rules;
using HandRaise.Host.Services;
using HandRaise.Infrastructure.Windows.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace HandRaise.Host.Tests;

public sealed class NotificationApiTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _destinationsStorePath;
    private readonly string _policiesStorePath;
    private readonly string _attemptsStorePath;

    public NotificationApiTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"notif_api_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _destinationsStorePath = Path.Combine(_tempDirectory, "notification-destinations.json");
        _policiesStorePath = Path.Combine(_tempDirectory, "notification-policies.json");
        _attemptsStorePath = Path.Combine(_tempDirectory, "notification-attempts.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task DestinationsAndPoliciesCrudEndToEnd()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        // 1. Initial destinations list is empty
        var initialDestList = await client.GetFromJsonAsync<List<NotificationDestination>>(
            "/api/v1/notifications/destinations", AnalyticJsonDefaults.Options);
        Assert.NotNull(initialDestList);
        Assert.Empty(initialDestList);

        // 2. Create invalid destination -> 400
        var invalidDest = new NotificationDestination(
            Id: "",
            Name: "",
            Type: NotificationDestinationType.Webhook,
            Enabled: true,
            Configuration: new Dictionary<string, object?>());
        var invalidDestResp = await client.PostAsJsonAsync(
            "/api/v1/notifications/destinations", invalidDest, AnalyticJsonDefaults.Options);
        Assert.Equal(HttpStatusCode.BadRequest, invalidDestResp.StatusCode);

        // 3. Create valid destination with secret -> 201 & redacted response
        var validDest = new NotificationDestination(
            Id: "dest-slack-1",
            Name: "Slack Alertas",
            Type: NotificationDestinationType.Webhook,
            Enabled: true,
            Configuration: new Dictionary<string, object?>
            {
                ["url"] = "https://hooks.slack.com/services/123",
                ["secret_token"] = "my-secret-token",
                ["headers"] = new Dictionary<string, object?> { ["Authorization"] = "Bearer token123" }
            });

        var createDestResp = await client.PostAsJsonAsync(
            "/api/v1/notifications/destinations", validDest, AnalyticJsonDefaults.Options);
        Assert.Equal(HttpStatusCode.Created, createDestResp.StatusCode);
        var createdDest = await createDestResp.Content.ReadFromJsonAsync<NotificationDestination>(AnalyticJsonDefaults.Options);
        Assert.NotNull(createdDest);
        Assert.Equal("dest-slack-1", createdDest!.Id);
        Assert.Equal("********", createdDest.Configuration!["secret_token"]?.ToString());

        // 4. Get destination by Id -> 200 & redacted
        var getDest = await client.GetFromJsonAsync<NotificationDestination>(
            "/api/v1/notifications/destinations/dest-slack-1", AnalyticJsonDefaults.Options);
        Assert.NotNull(getDest);
        Assert.Equal("********", getDest!.Configuration!["secret_token"]?.ToString());

        // 5. Test destination endpoint -> 200
        var testResp = await client.PostAsync("/api/v1/notifications/destinations/dest-slack-1/test", null);
        Assert.Equal(HttpStatusCode.OK, testResp.StatusCode);
        var attempt = await testResp.Content.ReadFromJsonAsync<NotificationAttempt>(AnalyticJsonDefaults.Options);
        Assert.NotNull(attempt);
        Assert.Equal("dest-slack-1", attempt!.DestinationId);

        // 6. Create valid policy -> 201
        var validPolicy = new NotificationPolicy(
            Id: "pol-high-alerts",
            Name: "Alertas Críticas y Altas",
            Enabled: true,
            DestinationId: "dest-slack-1",
            SeverityFilter: [AlertSeverity.High, AlertSeverity.Critical]);

        var createPolResp = await client.PostAsJsonAsync(
            "/api/v1/notifications/policies", validPolicy, AnalyticJsonDefaults.Options);
        Assert.Equal(HttpStatusCode.Created, createPolResp.StatusCode);
        var createdPol = await createPolResp.Content.ReadFromJsonAsync<NotificationPolicy>(AnalyticJsonDefaults.Options);
        Assert.NotNull(createdPol);
        Assert.Equal("pol-high-alerts", createdPol!.Id);

        // 7. Get policies list -> 200
        var policiesList = await client.GetFromJsonAsync<List<NotificationPolicy>>(
            "/api/v1/notifications/policies", AnalyticJsonDefaults.Options);
        Assert.NotNull(policiesList);
        Assert.Single(policiesList);

        // 8. Query attempts -> 200
        var attemptsList = await client.GetFromJsonAsync<List<NotificationAttempt>>(
            "/api/v1/notifications/attempts", AnalyticJsonDefaults.Options);
        Assert.NotNull(attemptsList);
        Assert.NotEmpty(attemptsList);

        // 9. Delete policy -> 204
        var delPolResp = await client.DeleteAsync("/api/v1/notifications/policies/pol-high-alerts");
        Assert.Equal(HttpStatusCode.NoContent, delPolResp.StatusCode);

        // 10. Delete destination -> 204
        var delDestResp = await client.DeleteAsync("/api/v1/notifications/destinations/dest-slack-1");
        Assert.Equal(HttpStatusCode.NoContent, delDestResp.StatusCode);
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
                ["storage:notificationDestinationsStorePath"] = _destinationsStorePath,
                ["storage:notificationPoliciesStorePath"] = _policiesStorePath,
                ["storage:notificationAttemptsStorePath"] = _attemptsStorePath
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
