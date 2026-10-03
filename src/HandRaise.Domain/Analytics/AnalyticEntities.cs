using System.Text.Json;
using System.Text.Json.Serialization;

namespace HandRaise.Domain.Analytics;

public sealed record ParameterOption(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("label")] string Label);

public sealed record ParameterDefinition(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("type")] ParameterType Type,
    [property: JsonPropertyName("default_value")] object? DefaultValue = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("min")] double? Min = null,
    [property: JsonPropertyName("max")] double? Max = null,
    [property: JsonPropertyName("step")] double? Step = null,
    [property: JsonPropertyName("options")] IReadOnlyList<ParameterOption>? Options = null);

public sealed record AnalyticDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("category")] AnalyticCategory Category,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("required_capabilities")] IReadOnlyList<InferenceCapability> RequiredCapabilities,
    [property: JsonPropertyName("features")] IReadOnlyList<AnalyticFeature> Features,
    [property: JsonPropertyName("produced_event_types")] IReadOnlyList<string> ProducedEventTypes,
    [property: JsonPropertyName("parameters")] IReadOnlyList<ParameterDefinition> Parameters);

public sealed record CameraAnalyticInstance(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("camera_id")] string CameraId,
    [property: JsonPropertyName("analytic_type_id")] string AnalyticTypeId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled = true,
    [property: JsonPropertyName("status")] AnalyticStatus Status = AnalyticStatus.Active,
    [property: JsonPropertyName("configuration")] IReadOnlyDictionary<string, object?>? Configuration = null,
    [property: JsonPropertyName("assigned_zone_ids")] IReadOnlyList<string>? AssignedZoneIds = null,
    [property: JsonPropertyName("assigned_line_ids")] IReadOnlyList<string>? AssignedLineIds = null,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt = null,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt = null);

public sealed record AnalyticEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("camera_id")] string CameraId,
    [property: JsonPropertyName("analytic_instance_id")] string AnalyticInstanceId,
    [property: JsonPropertyName("analytic_type")] string AnalyticType,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("track_id")] int? TrackId = null,
    [property: JsonPropertyName("zone_id")] string? ZoneId = null,
    [property: JsonPropertyName("confidence")] double? Confidence = null,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, object?>? Metadata = null,
    [property: JsonPropertyName("snapshot_url")] string? SnapshotUrl = null,
    [property: JsonPropertyName("node_id")] string NodeId = "unknown",
    [property: JsonPropertyName("site_id")] string SiteId = "unknown")
{
    [JsonIgnore]
    public byte[]? SnapshotJpeg { get; init; }
}

public static class AnalyticJsonDefaults
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };
}
