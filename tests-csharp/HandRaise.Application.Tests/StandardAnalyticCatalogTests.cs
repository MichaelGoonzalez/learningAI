using System.Text.Json;
using HandRaise.Application.Analytics;
using HandRaise.Domain.Analytics;

namespace HandRaise.Application.Tests;

public sealed class StandardAnalyticCatalogTests
{
    private readonly StandardAnalyticCatalog _catalog = new();

    [Fact]
    public void CatalogContainsHandRaiseDefinition()
    {
        var all = _catalog.GetAll();
        Assert.NotEmpty(all);
        var handRaise = _catalog.GetById("hand_raise");
        Assert.NotNull(handRaise);
        Assert.Equal("hand_raise", handRaise.Id);
        Assert.Equal(AnalyticCategory.Operations, handRaise.Category);
        Assert.Contains("hand_raised", handRaise.ProducedEventTypes);
        Assert.Contains("hand_lowered", handRaise.ProducedEventTypes);
        Assert.Contains(InferenceCapability.PoseEstimation, handRaise.RequiredCapabilities);
        Assert.Contains(InferenceCapability.Tracking, handRaise.RequiredCapabilities);
    }

    [Fact]
    public void ValidateConfigurationAcceptsValidParameters()
    {
        var config = new Dictionary<string, object?>
        {
            ["strict_mode"] = true,
            ["consecutive_frames"] = 5,
            ["cooldown_ms"] = 1500
        };

        var (isValid, errorMessage) = _catalog.ValidateConfiguration("hand_raise", config);
        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ValidateConfigurationAcceptsJsonElements()
    {
        using var doc = JsonDocument.Parse("""
        {
            "strict_mode": false,
            "consecutive_frames": 4,
            "cooldown_ms": 2000
        }
        """);

        var config = new Dictionary<string, object?>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            config[prop.Name] = prop.Value;
        }

        var (isValid, errorMessage) = _catalog.ValidateConfiguration("hand_raise", config);
        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ValidateConfigurationRejectsUnknownAnalyticType()
    {
        var (isValid, errorMessage) = _catalog.ValidateConfiguration("unknown_analytic", null);
        Assert.False(isValid);
        Assert.NotNull(errorMessage);
        Assert.Contains("no está soportado", errorMessage);
    }

    [Fact]
    public void ValidateConfigurationRejectsUnknownParameter()
    {
        var config = new Dictionary<string, object?>
        {
            ["invalid_param"] = 123
        };

        var (isValid, errorMessage) = _catalog.ValidateConfiguration("hand_raise", config);
        Assert.False(isValid);
        Assert.Contains("no está definido", errorMessage);
    }

    [Fact]
    public void ValidateConfigurationRejectsInvalidType()
    {
        var config = new Dictionary<string, object?>
        {
            ["strict_mode"] = "not-a-bool"
        };

        var (isValid, errorMessage) = _catalog.ValidateConfiguration("hand_raise", config);
        Assert.False(isValid);
        Assert.Contains("booleano", errorMessage);
    }

    [Fact]
    public void ValidateConfigurationRejectsOutOfRangeNumbers()
    {
        var configLow = new Dictionary<string, object?> { ["consecutive_frames"] = 0 };
        var (isLowValid, errorLow) = _catalog.ValidateConfiguration("hand_raise", configLow);
        Assert.False(isLowValid);
        Assert.Contains("menor", errorLow);

        var configHigh = new Dictionary<string, object?> { ["consecutive_frames"] = 50 };
        var (isHighValid, errorHigh) = _catalog.ValidateConfiguration("hand_raise", configHigh);
        Assert.False(isHighValid);
        Assert.Contains("mayor", errorHigh);
    }

    [Fact]
    public void CatalogContainsPersonPresenceDefinition()
    {
        var personPresence = _catalog.GetById("person_presence");
        Assert.NotNull(personPresence);
        Assert.Equal("person_presence", personPresence.Id);
        Assert.Equal("Presencia de Personas", personPresence.DisplayName);
        Assert.Equal(AnalyticCategory.Operations, personPresence.Category);
        Assert.Contains("person_presence_started", personPresence.ProducedEventTypes);
        Assert.Contains("person_presence_ended", personPresence.ProducedEventTypes);
        Assert.Contains(InferenceCapability.ObjectDetection, personPresence.RequiredCapabilities);
        Assert.Contains(InferenceCapability.Tracking, personPresence.RequiredCapabilities);
        Assert.Contains(AnalyticFeature.Zones, personPresence.Features);
        Assert.Contains(AnalyticFeature.Sensitivity, personPresence.Features);
        Assert.Contains(AnalyticFeature.Snapshots, personPresence.Features);
    }

    [Fact]
    public void ValidateConfigurationAcceptsValidPersonPresenceParameters()
    {
        var config = new Dictionary<string, object?>
        {
            ["confidence_threshold"] = 0.75,
            ["min_presence_ms"] = 2000,
            ["absence_grace_ms"] = 3000,
            ["emit_snapshot"] = false
        };

        var (isValid, errorMessage) = _catalog.ValidateConfiguration("person_presence", config);
        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ValidateConfigurationRejectsOutOfRangePersonPresenceParameters()
    {
        var lowConf = new Dictionary<string, object?> { ["confidence_threshold"] = 0.05 };
        var (isLowValid, errorLow) = _catalog.ValidateConfiguration("person_presence", lowConf);
        Assert.False(isLowValid);
        Assert.Contains("menor", errorLow);

        var highMin = new Dictionary<string, object?> { ["min_presence_ms"] = 70000 };
        var (isHighValid, errorHigh) = _catalog.ValidateConfiguration("person_presence", highMin);
        Assert.False(isHighValid);
        Assert.Contains("mayor", errorHigh);
    }

    [Fact]
    public void PersonPresenceDefinitionSerializesToSnakeCaseJson()
    {
        var personPresence = _catalog.GetById("person_presence");
        var json = JsonSerializer.Serialize(personPresence, AnalyticJsonDefaults.Options);

        Assert.Contains("\"id\":\"person_presence\"", json);
        Assert.Contains("\"display_name\":\"Presencia de Personas\"", json);
        Assert.Contains("\"category\":\"operations\"", json);
        Assert.Contains("\"produced_event_types\":[\"person_presence_started\",\"person_presence_ended\"]", json);
        Assert.Contains("\"confidence_threshold\"", json);
        Assert.Contains("\"min_presence_ms\"", json);
        Assert.Contains("\"absence_grace_ms\"", json);
        Assert.Contains("\"emit_snapshot\"", json);
    }

    [Fact]
    public void CatalogContainsZoneIntrusionDefinition()
    {
        var zoneIntrusion = _catalog.GetById("zone_intrusion");
        Assert.NotNull(zoneIntrusion);
        Assert.Equal("zone_intrusion", zoneIntrusion.Id);
        Assert.Equal("Intrusión en Zona Restringida", zoneIntrusion.DisplayName);
        Assert.Equal(AnalyticCategory.Security, zoneIntrusion.Category);
        Assert.Contains("zone_intrusion_started", zoneIntrusion.ProducedEventTypes);
        Assert.Contains("zone_intrusion_ended", zoneIntrusion.ProducedEventTypes);
        Assert.Contains(InferenceCapability.ObjectDetection, zoneIntrusion.RequiredCapabilities);
        Assert.Contains(InferenceCapability.Tracking, zoneIntrusion.RequiredCapabilities);
        Assert.Contains(AnalyticFeature.Zones, zoneIntrusion.Features);
        Assert.Contains(AnalyticFeature.Sensitivity, zoneIntrusion.Features);
        Assert.Contains(AnalyticFeature.Snapshots, zoneIntrusion.Features);
        Assert.Contains(AnalyticFeature.Tracking, zoneIntrusion.Features);
        Assert.Contains(AnalyticFeature.Schedules, zoneIntrusion.Features);
    }

    [Fact]
    public void ValidateConfigurationAcceptsValidZoneIntrusionParameters()
    {
        var config = new Dictionary<string, object?>
        {
            ["confidence_threshold"] = 0.70,
            ["entry_delay_ms"] = 1000,
            ["exit_grace_ms"] = 2000,
            ["emit_snapshot"] = true
        };

        var (isValid, errorMessage) = _catalog.ValidateConfiguration("zone_intrusion", config);
        Assert.True(isValid);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void ValidateConfigurationRejectsOutOfRangeZoneIntrusionParameters()
    {
        var lowConf = new Dictionary<string, object?> { ["confidence_threshold"] = 0.01 };
        var (isLowValid, errorLow) = _catalog.ValidateConfiguration("zone_intrusion", lowConf);
        Assert.False(isLowValid);
        Assert.Contains("menor", errorLow);

        var highDelay = new Dictionary<string, object?> { ["entry_delay_ms"] = 70000 };
        var (isHighValid, errorHigh) = _catalog.ValidateConfiguration("zone_intrusion", highDelay);
        Assert.False(isHighValid);
        Assert.Contains("mayor", errorHigh);
    }

    [Fact]
    public void ZoneIntrusionDefinitionSerializesToSnakeCaseJson()
    {
        var zoneIntrusion = _catalog.GetById("zone_intrusion");
        var json = JsonSerializer.Serialize(zoneIntrusion, AnalyticJsonDefaults.Options);

        Assert.Contains("\"id\":\"zone_intrusion\"", json);
        Assert.Contains("\"display_name\"", json);
        Assert.Contains("\"category\":\"security\"", json);
        Assert.Contains("\"produced_event_types\":[\"zone_intrusion_started\",\"zone_intrusion_ended\"]", json);
        Assert.Contains("\"confidence_threshold\"", json);
        Assert.Contains("\"entry_delay_ms\"", json);
        Assert.Contains("\"exit_grace_ms\"", json);
        Assert.Contains("\"emit_snapshot\"", json);

        var deserialized = JsonSerializer.Deserialize<AnalyticDefinition>(json, AnalyticJsonDefaults.Options);
        Assert.NotNull(deserialized);
        Assert.Equal("Intrusión en Zona Restringida", deserialized.DisplayName);
    }
}
