using System.Text.Json.Serialization;

namespace HandRaise.Application.Events;

public sealed record HandEvent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("camera_id")] string CameraId,
    [property: JsonPropertyName("track_id")] int TrackId,
    [property: JsonPropertyName("hand")] string Hand,
    [property: JsonPropertyName("zone")] string? Zone,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("timestamp")] DateTimeOffset Timestamp,
    [property: JsonPropertyName("snapshot_url")] string? SnapshotUrl = null,
    [property: JsonPropertyName("node_id")] string NodeId = "unknown",
    [property: JsonPropertyName("site_id")] string SiteId = "unknown",
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, object?>? Metadata = null)
{
    [JsonIgnore]
    public byte[]? SnapshotJpeg { get; init; }
}
