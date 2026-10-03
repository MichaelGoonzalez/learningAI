using HandRaise.Application.Notifications;
using HandRaise.Domain.Notifications;
using HandRaise.Domain.Rules;
using HandRaise.Infrastructure.Windows.Storage;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class JsonNotificationStoresTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _destinationsPath;
    private readonly string _policiesPath;
    private readonly string _attemptsPath;

    public JsonNotificationStoresTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"notif_store_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _destinationsPath = Path.Combine(_tempDirectory, "notification-destinations.json");
        _policiesPath = Path.Combine(_tempDirectory, "notification-policies.json");
        _attemptsPath = Path.Combine(_tempDirectory, "notification-attempts.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task DestinationStore_CrudAndRedactionWorkCorrectly()
    {
        var store = new JsonNotificationDestinationStore(_destinationsPath);

        var dest = new NotificationDestination(
            Id: "dest-test-1",
            Name: "Slack Webhook",
            Type: NotificationDestinationType.Webhook,
            Enabled: true,
            Configuration: new Dictionary<string, object?>
            {
                ["url"] = "https://hooks.slack.com/services/123",
                ["secret_token"] = "super-secret-token",
                ["headers"] = new Dictionary<string, object?>
                {
                    ["Authorization"] = "Bearer top-secret",
                    ["X-Custom"] = "visible-value"
                }
            });

        var saved = await store.SaveAsync(dest);
        Assert.NotNull(saved);

        var retrieved = await store.GetByIdAsync("dest-test-1");
        Assert.NotNull(retrieved);
        Assert.Equal("Slack Webhook", retrieved!.Name);

        // Test Redaction
        var redacted = JsonNotificationDestinationStore.Redact(retrieved);
        Assert.Equal("********", redacted.Configuration!["secret_token"]);

        var headers = (Dictionary<string, object?>)redacted.Configuration["headers"]!;
        Assert.Equal("********", headers["Authorization"]);
        Assert.Equal("visible-value", headers["X-Custom"]);

        // Delete
        var deleted = await store.DeleteAsync("dest-test-1");
        Assert.True(deleted);

        var afterDelete = await store.GetByIdAsync("dest-test-1");
        Assert.Null(afterDelete);
    }

    [Fact]
    public async Task PolicyStore_CrudWorksCorrectly()
    {
        var store = new JsonNotificationPolicyStore(_policiesPath);

        var policy = new NotificationPolicy(
            Id: "pol-1",
            Name: "Policy Test",
            Enabled: true,
            DestinationId: "dest-1",
            SeverityFilter: [AlertSeverity.High]);

        await store.SaveAsync(policy);

        var list = await store.ListAllAsync();
        Assert.Single(list);
        Assert.Equal("pol-1", list[0].Id);

        var retrieved = await store.GetByIdAsync("pol-1");
        Assert.NotNull(retrieved);
        Assert.Equal("Policy Test", retrieved!.Name);

        await store.DeleteAsync("pol-1");
        var listAfter = await store.ListAllAsync();
        Assert.Empty(listAfter);
    }

    [Fact]
    public async Task AttemptStore_SaveAndQueryWorksCorrectly()
    {
        var store = new JsonNotificationAttemptStore(_attemptsPath);

        var attempt = new NotificationAttempt(
            Id: "att-001",
            AlertId: "alt-001",
            PolicyId: "pol-1",
            DestinationId: "dest-1",
            AttemptNumber: 1,
            Status: NotificationStatus.Succeeded,
            TimestampUtc: DateTimeOffset.UtcNow,
            ResponseCode: 200);

        await store.SaveAsync(attempt);

        var queried = await store.QueryAsync(new NotificationAttemptQuery(AlertId: "alt-001"));
        Assert.Single(queried);
        Assert.Equal("att-001", queried[0].Id);
        Assert.Equal(NotificationStatus.Succeeded, queried[0].Status);

        var queryNone = await store.QueryAsync(new NotificationAttemptQuery(Status: NotificationStatus.Failed));
        Assert.Empty(queryNone);
    }
}
