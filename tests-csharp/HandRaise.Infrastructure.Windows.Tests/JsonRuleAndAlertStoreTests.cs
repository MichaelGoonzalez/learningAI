using HandRaise.Application.Rules;
using HandRaise.Domain.Rules;
using HandRaise.Infrastructure.Windows.Storage;

namespace HandRaise.Infrastructure.Windows.Tests;

public sealed class JsonRuleAndAlertStoreTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _rulesPath;
    private readonly string _alertsPath;

    public JsonRuleAndAlertStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"rule_alert_store_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _rulesPath = Path.Combine(_tempDirectory, "rules.json");
        _alertsPath = Path.Combine(_tempDirectory, "alerts.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, true);
        }
    }

    [Fact]
    public async Task JsonRuleStore_CrudOperationsWorkCorrectly()
    {
        var store = new JsonRuleStore(_rulesPath);

        var initial = await store.ListAllAsync();
        Assert.Empty(initial);

        var rule1 = new AlertRule(
            Id: "rule-1",
            CameraId: "cam-1",
            AnalyticInstanceId: "inst-1",
            Name: "Alerta 1",
            Enabled: true,
            Severity: AlertSeverity.High,
            EventTypes: ["person_presence_started"],
            CooldownMs: 1000);

        var saved = await store.SaveAsync(rule1);
        Assert.NotNull(saved);
        Assert.NotNull(saved.CreatedAt);

        var byInstance = await store.ListByInstanceAsync("cam-1", "inst-1");
        Assert.Single(byInstance);
        Assert.Equal("rule-1", byInstance[0].Id);

        var retrieved = await store.GetByIdAsync("cam-1", "inst-1", "rule-1");
        Assert.NotNull(retrieved);
        Assert.Equal("Alerta 1", retrieved!.Name);

        // Delete
        var deleted = await store.DeleteAsync("cam-1", "inst-1", "rule-1");
        Assert.True(deleted);

        var afterDelete = await store.ListByInstanceAsync("cam-1", "inst-1");
        Assert.Empty(afterDelete);
    }

    [Fact]
    public async Task JsonAlertStore_SaveQueryAcknowledgeResolveWorks()
    {
        var store = new JsonAlertStore(_alertsPath);

        var now = DateTimeOffset.UtcNow;
        var alert1 = new OperationalAlert(
            Id: "alt-01",
            RuleId: "rule-01",
            CameraId: "cam-1",
            AnalyticInstanceId: "inst-1",
            SourceEventId: "ev-01",
            Severity: AlertSeverity.High,
            Status: AlertStatus.Open,
            Title: "Alerta 1",
            Description: "Desc 1",
            CreatedAt: now);

        await store.SaveAsync(alert1);

        var queried = await store.QueryAsync(new AlertQuery(CameraId: "cam-1"));
        Assert.Single(queried);
        Assert.Equal("alt-01", queried[0].Id);
        Assert.Equal(AlertStatus.Open, queried[0].Status);

        // Acknowledge
        var acked = await store.AcknowledgeAsync("alt-01", now.AddMinutes(1));
        Assert.NotNull(acked);
        Assert.Equal(AlertStatus.Acknowledged, acked!.Status);
        Assert.NotNull(acked.AcknowledgedAt);

        // Resolve
        var resolved = await store.ResolveAsync("alt-01", now.AddMinutes(2));
        Assert.NotNull(resolved);
        Assert.Equal(AlertStatus.Resolved, resolved!.Status);
        Assert.NotNull(resolved.ResolvedAt);

        // ExistsForEventAndRuleAsync
        var exists = await store.ExistsForEventAndRuleAsync("ev-01", "rule-01");
        Assert.True(exists);
        var notExists = await store.ExistsForEventAndRuleAsync("ev-99", "rule-01");
        Assert.False(notExists);
    }
}
