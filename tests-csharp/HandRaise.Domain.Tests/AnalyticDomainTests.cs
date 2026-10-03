using System.Text.Json;
using HandRaise.Domain.Analytics;

namespace HandRaise.Domain.Tests;

public sealed class AnalyticDomainTests
{
    [Fact]
    public void AnalyticDefinition_InitializesAndSerializesToSnakeCaseJson()
    {
        var parameters = new List<ParameterDefinition>
        {
            new(
                Key: "consecutive_frames",
                Label: "Frames Consecutivos",
                Type: ParameterType.Number,
                DefaultValue: 3,
                Description: "Frames requeridos",
                Min: 1,
                Max: 30,
                Step: 1),
            new(
                Key: "mode",
                Label: "Modo",
                Type: ParameterType.Select,
                DefaultValue: "standard",
                Options: [new ParameterOption("standard", "Estándar"), new ParameterOption("strict", "Estricto")])
        };

        var def = new AnalyticDefinition(
            Id: "hand_raise",
            DisplayName: "Mano Levantada",
            Description: "Detecta gestos de mano levantada por persona",
            Category: AnalyticCategory.Operations,
            Version: "1.0.0",
            RequiredCapabilities: [InferenceCapability.PoseEstimation, InferenceCapability.Tracking],
            Features: [AnalyticFeature.Zones, AnalyticFeature.Snapshots, AnalyticFeature.Tracking],
            ProducedEventTypes: ["hand_raised", "hand_lowered"],
            Parameters: parameters);

        Assert.Equal("hand_raise", def.Id);
        Assert.Equal("Mano Levantada", def.DisplayName);
        Assert.Equal(AnalyticCategory.Operations, def.Category);
        Assert.Equal(2, def.RequiredCapabilities.Count);
        Assert.Equal(3, def.Features.Count);
        Assert.Equal(2, def.ProducedEventTypes.Count);
        Assert.Equal(2, def.Parameters.Count);

        var json = JsonSerializer.Serialize(def, AnalyticJsonDefaults.Options);
        Assert.Contains("\"id\":\"hand_raise\"", json);
        Assert.Contains("\"display_name\":\"Mano Levantada\"", json);
        Assert.Contains("\"category\":\"operations\"", json);
        Assert.Contains("\"required_capabilities\":[\"pose_estimation\",\"tracking\"]", json);
        Assert.Contains("\"features\":[\"zones\",\"snapshots\",\"tracking\"]", json);
        Assert.Contains("\"type\":\"number\"", json);
        Assert.Contains("\"type\":\"select\"", json);

        var deserialized = JsonSerializer.Deserialize<AnalyticDefinition>(json, AnalyticJsonDefaults.Options);
        Assert.NotNull(deserialized);
        Assert.Equal(def.Id, deserialized!.Id);
        Assert.Equal(AnalyticCategory.Operations, deserialized.Category);
        Assert.Equal(InferenceCapability.PoseEstimation, deserialized.RequiredCapabilities[0]);
        Assert.Equal(AnalyticFeature.Zones, deserialized.Features[0]);
    }

    [Fact]
    public void CameraAnalyticInstance_InitializesAndSerializesCorrectly()
    {
        var now = DateTimeOffset.UtcNow;
        var instance = new CameraAnalyticInstance(
            Id: "inst-1",
            CameraId: "cam-01",
            AnalyticTypeId: "hand_raise",
            Name: "Mano Recepción",
            Enabled: true,
            Status: AnalyticStatus.Active,
            Configuration: new Dictionary<string, object?> { ["consecutive_frames"] = 3, ["strict"] = false },
            AssignedZoneIds: ["zone-1", "zone-2"],
            AssignedLineIds: [],
            CreatedAt: now,
            UpdatedAt: now);

        Assert.Equal("inst-1", instance.Id);
        Assert.Equal("cam-01", instance.CameraId);
        Assert.Equal("hand_raise", instance.AnalyticTypeId);
        Assert.True(instance.Enabled);
        Assert.Equal(AnalyticStatus.Active, instance.Status);

        var json = JsonSerializer.Serialize(instance, AnalyticJsonDefaults.Options);
        Assert.Contains("\"analytic_type_id\":\"hand_raise\"", json);
        Assert.Contains("\"status\":\"active\"", json);
        Assert.Contains("\"assigned_zone_ids\":[\"zone-1\",\"zone-2\"]", json);

        var deserialized = JsonSerializer.Deserialize<CameraAnalyticInstance>(json, AnalyticJsonDefaults.Options);
        Assert.NotNull(deserialized);
        Assert.Equal(instance.Id, deserialized!.Id);
        Assert.Equal(AnalyticStatus.Active, deserialized.Status);
        Assert.Equal(2, deserialized.AssignedZoneIds?.Count);
    }

    [Fact]
    public void AnalyticEvent_ConstructsImmutableEventAndSerializesHeterogeneousMetadata()
    {
        var timestamp = DateTimeOffset.UtcNow;
        var ev = new AnalyticEvent(
            Id: "ev-100",
            CameraId: "cam-01",
            AnalyticInstanceId: "inst-1",
            AnalyticType: "hand_raise",
            EventType: "hand_raised",
            TimestampUtc: timestamp,
            TrackId: 10,
            ZoneId: "zone-1",
            Confidence: 0.92,
            Metadata: new Dictionary<string, object?>
            {
                ["hand"] = "left",
                ["score"] = 0.95,
                ["is_verified"] = true
            },
            SnapshotUrl: "/api/v1/events/ev-100/snapshot",
            NodeId: "node-1",
            SiteId: "site-1");

        Assert.Equal("ev-100", ev.Id);
        Assert.Equal("cam-01", ev.CameraId);
        Assert.Equal("hand_raised", ev.EventType);
        Assert.Equal(10, ev.TrackId);
        Assert.Equal("left", ev.Metadata?["hand"]);

        var json = JsonSerializer.Serialize(ev, AnalyticJsonDefaults.Options);
        Assert.Contains("\"analytic_type\":\"hand_raise\"", json);
        Assert.Contains("\"event_type\":\"hand_raised\"", json);
        Assert.Contains("\"timestamp_utc\":", json);
        Assert.Contains("\"metadata\":{", json);

        var deserialized = JsonSerializer.Deserialize<AnalyticEvent>(json, AnalyticJsonDefaults.Options);
        Assert.NotNull(deserialized);
        Assert.Equal(ev.Id, deserialized!.Id);
        Assert.Equal(ev.EventType, deserialized.EventType);
        Assert.NotNull(deserialized.Metadata);
    }

    [Theory]
    [InlineData(AnalyticStatus.Pending, "\"pending\"")]
    [InlineData(AnalyticStatus.Active, "\"active\"")]
    [InlineData(AnalyticStatus.Degraded, "\"degraded\"")]
    [InlineData(AnalyticStatus.Inactive, "\"inactive\"")]
    [InlineData(AnalyticStatus.Error, "\"error\"")]
    [InlineData(AnalyticStatus.Unsupported, "\"unsupported\"")]
    public void AnalyticStatus_SerializesToSnakeCase(AnalyticStatus status, string expectedJson)
    {
        var json = JsonSerializer.Serialize(status, AnalyticJsonDefaults.Options);
        Assert.Equal(expectedJson, json);
    }

    [Theory]
    [InlineData(InferenceCapability.ObjectDetection, "\"object_detection\"")]
    [InlineData(InferenceCapability.PoseEstimation, "\"pose_estimation\"")]
    [InlineData(InferenceCapability.Tracking, "\"tracking\"")]
    [InlineData(InferenceCapability.Classification, "\"classification\"")]
    [InlineData(InferenceCapability.Segmentation, "\"segmentation\"")]
    public void InferenceCapability_SerializesToSnakeCase(InferenceCapability capability, string expectedJson)
    {
        var json = JsonSerializer.Serialize(capability, AnalyticJsonDefaults.Options);
        Assert.Equal(expectedJson, json);
    }

    [Theory]
    [InlineData(AnalyticFeature.Zones, "\"zones\"")]
    [InlineData(AnalyticFeature.Lines, "\"lines\"")]
    [InlineData(AnalyticFeature.Points, "\"points\"")]
    [InlineData(AnalyticFeature.Sensitivity, "\"sensitivity\"")]
    [InlineData(AnalyticFeature.Snapshots, "\"snapshots\"")]
    [InlineData(AnalyticFeature.Tracking, "\"tracking\"")]
    [InlineData(AnalyticFeature.Schedules, "\"schedules\"")]
    public void AnalyticFeature_SerializesToSnakeCase(AnalyticFeature feature, string expectedJson)
    {
        var json = JsonSerializer.Serialize(feature, AnalyticJsonDefaults.Options);
        Assert.Equal(expectedJson, json);
    }
}
