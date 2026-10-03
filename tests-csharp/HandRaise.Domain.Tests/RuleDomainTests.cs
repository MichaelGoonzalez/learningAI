using System.Text.Json;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Rules;

namespace HandRaise.Domain.Tests;

public sealed class RuleDomainTests
{
    [Theory]
    [InlineData(AlertSeverity.Low, "\"low\"")]
    [InlineData(AlertSeverity.Medium, "\"medium\"")]
    [InlineData(AlertSeverity.High, "\"high\"")]
    [InlineData(AlertSeverity.Critical, "\"critical\"")]
    public void AlertSeverity_SerializesToSnakeCase(AlertSeverity severity, string expectedJson)
    {
        var json = JsonSerializer.Serialize(severity, AnalyticJsonDefaults.Options);
        Assert.Equal(expectedJson, json);

        var deserialized = JsonSerializer.Deserialize<AlertSeverity>(expectedJson, AnalyticJsonDefaults.Options);
        Assert.Equal(severity, deserialized);
    }

    [Theory]
    [InlineData(AlertStatus.Open, "\"open\"")]
    [InlineData(AlertStatus.Acknowledged, "\"acknowledged\"")]
    [InlineData(AlertStatus.Resolved, "\"resolved\"")]
    public void AlertStatus_SerializesToSnakeCase(AlertStatus status, string expectedJson)
    {
        var json = JsonSerializer.Serialize(status, AnalyticJsonDefaults.Options);
        Assert.Equal(expectedJson, json);

        var deserialized = JsonSerializer.Deserialize<AlertStatus>(expectedJson, AnalyticJsonDefaults.Options);
        Assert.Equal(status, deserialized);
    }

    [Theory]
    [InlineData(RuleOperator.Equals, "\"equals\"")]
    [InlineData(RuleOperator.NotEquals, "\"not_equals\"")]
    [InlineData(RuleOperator.GreaterThan, "\"greater_than\"")]
    [InlineData(RuleOperator.GreaterOrEqual, "\"greater_or_equal\"")]
    [InlineData(RuleOperator.LessThan, "\"less_than\"")]
    [InlineData(RuleOperator.LessOrEqual, "\"less_or_equal\"")]
    [InlineData(RuleOperator.Contains, "\"contains\"")]
    public void RuleOperator_SerializesToSnakeCase(RuleOperator op, string expectedJson)
    {
        var json = JsonSerializer.Serialize(op, AnalyticJsonDefaults.Options);
        Assert.Equal(expectedJson, json);

        var deserialized = JsonSerializer.Deserialize<RuleOperator>(expectedJson, AnalyticJsonDefaults.Options);
        Assert.Equal(op, deserialized);
    }

    [Fact]
    public void RuleCondition_Validate_SucceedsForValidCondition()
    {
        var condition = new RuleCondition(
            Field: "current_occupancy",
            Operator: RuleOperator.GreaterOrEqual,
            Value: 50);

        var exception = Record.Exception(() => condition.Validate());
        Assert.Null(exception);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RuleCondition_Validate_ThrowsWhenFieldIsInvalid(string? invalidField)
    {
        var condition = new RuleCondition(
            Field: invalidField!,
            Operator: RuleOperator.Equals,
            Value: "test");

        Assert.Throws<ArgumentException>(() => condition.Validate());
    }

    [Fact]
    public void RuleCondition_Validate_ThrowsWhenValueIsNull()
    {
        var condition = new RuleCondition(
            Field: "zone_id",
            Operator: RuleOperator.Equals,
            Value: null!);

        Assert.Throws<ArgumentException>(() => condition.Validate());
    }

    [Fact]
    public void AlertRule_Validate_SucceedsForValidRule()
    {
        var rule = new AlertRule(
            Id: "rule-1",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-01",
            Name: "Alerta de Aforo",
            Enabled: true,
            Severity: AlertSeverity.High,
            EventTypes: ["person_count_updated"],
            Conditions: [new RuleCondition("current_occupancy", RuleOperator.GreaterOrEqual, 50)],
            CooldownMs: 5000,
            TitleTemplate: "Aforo excedido en {camera_id}",
            DescriptionTemplate: "Ocupación actual: {metadata.current_occupancy}");

        var exception = Record.Exception(() => rule.Validate());
        Assert.Null(exception);
    }

    [Theory]
    [InlineData("", "cam-01", "rule-name")]
    [InlineData("rule-1", "", "rule-name")]
    [InlineData("rule-1", "cam-01", "")]
    public void AlertRule_Validate_ThrowsWhenRequiredFieldsMissing(string id, string cameraId, string name)
    {
        var rule = new AlertRule(
            Id: id,
            CameraId: cameraId,
            AnalyticInstanceId: "inst-01",
            Name: name,
            Enabled: true,
            Severity: AlertSeverity.Medium,
            EventTypes: ["test_event"]);

        Assert.Throws<ArgumentException>(() => rule.Validate());
    }

    [Fact]
    public void AlertRule_Validate_ThrowsWhenEventTypesEmpty()
    {
        var rule = new AlertRule(
            Id: "rule-1",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-01",
            Name: "Test Rule",
            Enabled: true,
            Severity: AlertSeverity.Low,
            EventTypes: []);

        Assert.Throws<ArgumentException>(() => rule.Validate());
    }

    [Fact]
    public void AlertRule_Validate_ThrowsWhenCooldownNegative()
    {
        var rule = new AlertRule(
            Id: "rule-1",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-01",
            Name: "Test Rule",
            Enabled: true,
            Severity: AlertSeverity.Low,
            EventTypes: ["test_event"],
            CooldownMs: -1);

        Assert.Throws<ArgumentException>(() => rule.Validate());
    }

    [Fact]
    public void OperationalAlert_SerializesToSnakeCaseJson()
    {
        var now = DateTimeOffset.UtcNow;
        var alert = new OperationalAlert(
            Id: "alert-001",
            RuleId: "rule-100",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-01",
            SourceEventId: "ev-500",
            Severity: AlertSeverity.Critical,
            Status: AlertStatus.Open,
            Title: "Intrusión detectada",
            Description: "Persona detectada en zona restringida",
            CreatedAt: now,
            AcknowledgedAt: null,
            ResolvedAt: null,
            Metadata: new Dictionary<string, object?> { ["zone_id"] = "restricted_zone" });

        var json = JsonSerializer.Serialize(alert, AnalyticJsonDefaults.Options);
        Assert.Contains("\"rule_id\":\"rule-100\"", json);
        Assert.Contains("\"camera_id\":\"cam-01\"", json);
        Assert.Contains("\"source_event_id\":\"ev-500\"", json);
        Assert.Contains("\"severity\":\"critical\"", json);
        Assert.Contains("\"status\":\"open\"", json);

        var deserialized = JsonSerializer.Deserialize<OperationalAlert>(json, AnalyticJsonDefaults.Options);
        Assert.NotNull(deserialized);
        Assert.Equal(alert.Id, deserialized!.Id);
        Assert.Equal("Intrusión detectada", deserialized.Title);
        Assert.Equal(AlertSeverity.Critical, deserialized.Severity);
        Assert.Equal(AlertStatus.Open, deserialized.Status);
    }
}
