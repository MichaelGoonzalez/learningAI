using System.Text.Json;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Notifications;
using HandRaise.Domain.Rules;

namespace HandRaise.Domain.Tests;

public sealed class NotificationDomainTests
{
    [Theory]
    [InlineData(NotificationDestinationType.Webhook, "\"webhook\"")]
    [InlineData(NotificationDestinationType.Mqtt, "\"mqtt\"")]
    [InlineData(NotificationDestinationType.Custom, "\"custom\"")]
    public void NotificationDestinationType_SerializesToSnakeCase(NotificationDestinationType type, string expectedJson)
    {
        var json = JsonSerializer.Serialize(type, AnalyticJsonDefaults.Options);
        Assert.Equal(expectedJson, json);

        var deserialized = JsonSerializer.Deserialize<NotificationDestinationType>(expectedJson, AnalyticJsonDefaults.Options);
        Assert.Equal(type, deserialized);
    }

    [Theory]
    [InlineData(NotificationStatus.Pending, "\"pending\"")]
    [InlineData(NotificationStatus.Succeeded, "\"succeeded\"")]
    [InlineData(NotificationStatus.Failed, "\"failed\"")]
    public void NotificationStatus_SerializesToSnakeCase(NotificationStatus status, string expectedJson)
    {
        var json = JsonSerializer.Serialize(status, AnalyticJsonDefaults.Options);
        Assert.Equal(expectedJson, json);

        var deserialized = JsonSerializer.Deserialize<NotificationStatus>(expectedJson, AnalyticJsonDefaults.Options);
        Assert.Equal(status, deserialized);
    }

    [Fact]
    public void NotificationDestination_Validate_SucceedsForValidWebhook()
    {
        var dest = new NotificationDestination(
            Id: "dest-wh-1",
            Name: "Slack Webhook",
            Type: NotificationDestinationType.Webhook,
            Enabled: true,
            Configuration: new Dictionary<string, object?>
            {
                ["url"] = "https://hooks.slack.com/services/T00/B00/X00",
                ["method"] = "POST",
                ["timeout_ms"] = 5000
            });

        var exception = Record.Exception(() => dest.Validate());
        Assert.Null(exception);
    }

    [Fact]
    public void NotificationDestination_Validate_ThrowsWhenMissingUrlForWebhook()
    {
        var dest = new NotificationDestination(
            Id: "dest-wh-1",
            Name: "Slack Webhook",
            Type: NotificationDestinationType.Webhook,
            Enabled: true,
            Configuration: new Dictionary<string, object?>());

        Assert.Throws<ArgumentException>(() => dest.Validate());
    }

    [Fact]
    public void NotificationDestination_Validate_ThrowsWhenInvalidUrl()
    {
        var dest = new NotificationDestination(
            Id: "dest-wh-1",
            Name: "Slack Webhook",
            Type: NotificationDestinationType.Webhook,
            Enabled: true,
            Configuration: new Dictionary<string, object?> { ["url"] = "ftp://invalid-url" });

        Assert.Throws<ArgumentException>(() => dest.Validate());
    }

    [Fact]
    public void NotificationDestination_Validate_SucceedsForValidMqtt()
    {
        var dest = new NotificationDestination(
            Id: "dest-mqtt-1",
            Name: "Local Mosquitto",
            Type: NotificationDestinationType.Mqtt,
            Enabled: true,
            Configuration: new Dictionary<string, object?>
            {
                ["broker"] = "192.168.1.50",
                ["port"] = 1883,
                ["topic"] = "visioncontrol/{node_id}/alerts"
            });

        var exception = Record.Exception(() => dest.Validate());
        Assert.Null(exception);
    }

    [Fact]
    public void NotificationPolicy_Matches_CorrectlyEvaluatesFilters()
    {
        var policy = new NotificationPolicy(
            Id: "pol-1",
            Name: "High Severity Intrusion Policy",
            Enabled: true,
            DestinationId: "dest-1",
            SeverityFilter: [AlertSeverity.High, AlertSeverity.Critical],
            CameraIds: ["cam-01", "cam-02"],
            AnalyticTypeIds: ["zone_intrusion"],
            AlertStatusFilter: [AlertStatus.Open]);

        var alertMatch = new OperationalAlert(
            Id: "alt-1",
            RuleId: "r-1",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-intrusion-1",
            SourceEventId: "ev-1",
            Severity: AlertSeverity.High,
            Status: AlertStatus.Open,
            Title: "Intrusión",
            Description: "Persona en bóveda",
            CreatedAt: DateTimeOffset.UtcNow);

        // Matching
        Assert.True(policy.Matches(alertMatch, "zone_intrusion"));

        // Severity mismatch (Low)
        var alertLow = alertMatch with { Severity = AlertSeverity.Low };
        Assert.False(policy.Matches(alertLow, "zone_intrusion"));

        // Camera mismatch (cam-99)
        var alertWrongCam = alertMatch with { CameraId = "cam-99" };
        Assert.False(policy.Matches(alertWrongCam, "zone_intrusion"));

        // Status mismatch (Resolved)
        var alertResolved = alertMatch with { Status = AlertStatus.Resolved };
        Assert.False(policy.Matches(alertResolved, "zone_intrusion"));

        // Analytic type mismatch
        Assert.False(policy.Matches(alertMatch, "person_presence"));
    }

    [Fact]
    public void NotificationPolicy_Matches_ReturnsFalseWhenDisabled()
    {
        var policy = new NotificationPolicy(
            Id: "pol-disabled",
            Name: "Disabled Policy",
            Enabled: false,
            DestinationId: "dest-1");

        var alert = new OperationalAlert(
            Id: "alt-1",
            RuleId: "r-1",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-1",
            SourceEventId: "ev-1",
            Severity: AlertSeverity.High,
            Status: AlertStatus.Open,
            Title: "Alerta",
            Description: "Desc",
            CreatedAt: DateTimeOffset.UtcNow);

        Assert.False(policy.Matches(alert));
    }

    [Fact]
    public void WebhookPayload_SerializesToSnakeCaseJson()
    {
        var now = DateTimeOffset.UtcNow;
        var alert = new OperationalAlert(
            Id: "alt-100",
            RuleId: "rule-100",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-01",
            SourceEventId: "ev-500",
            Severity: AlertSeverity.Critical,
            Status: AlertStatus.Open,
            Title: "Alerta",
            Description: "Desc",
            CreatedAt: now);

        var payload = new WebhookPayload(
            SchemaVersion: "1.0",
            NodeId: "node-edge-1",
            SiteId: "site-main",
            Alert: alert,
            DispatchedAt: now,
            IsTest: false);

        var json = JsonSerializer.Serialize(payload, AnalyticJsonDefaults.Options);
        Assert.Contains("\"schema_version\":\"1.0\"", json);
        Assert.Contains("\"node_id\":\"node-edge-1\"", json);
        Assert.Contains("\"site_id\":\"site-main\"", json);
        Assert.Contains("\"alert\":{", json);
        Assert.Contains("\"is_test\":false", json);

        var deserialized = JsonSerializer.Deserialize<WebhookPayload>(json, AnalyticJsonDefaults.Options);
        Assert.NotNull(deserialized);
        Assert.Equal("1.0", deserialized!.SchemaVersion);
        Assert.Equal("node-edge-1", deserialized.NodeId);
        Assert.Equal("alt-100", deserialized.Alert.Id);
    }
}
