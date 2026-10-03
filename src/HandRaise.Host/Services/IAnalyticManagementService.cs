using System.Text.Json.Serialization;
using HandRaise.Application.Analytics;
using HandRaise.Domain.Analytics;

namespace HandRaise.Host.Services;

public sealed record CameraAnalyticWriteRequest(
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("analytic_type_id")] string? AnalyticTypeId = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("enabled")] bool? Enabled = null,
    [property: JsonPropertyName("configuration")] IReadOnlyDictionary<string, object?>? Configuration = null,
    [property: JsonPropertyName("assigned_zone_ids")] IReadOnlyList<string>? AssignedZoneIds = null,
    [property: JsonPropertyName("assigned_line_ids")] IReadOnlyList<string>? AssignedLineIds = null);

public interface IAnalyticManagementService
{
    IReadOnlyList<AnalyticDefinition> GetCatalog();
    Task<IReadOnlyList<CameraAnalyticInstance>?> ListByCameraAsync(string cameraId, CancellationToken token = default);
    Task<CameraAnalyticInstance?> GetAsync(string cameraId, string instanceId, CancellationToken token = default);
    Task<CameraAnalyticInstance> CreateAsync(string cameraId, CameraAnalyticWriteRequest request, CancellationToken token = default);
    Task<CameraAnalyticInstance?> UpdateAsync(string cameraId, string instanceId, CameraAnalyticWriteRequest request, CancellationToken token = default);
    Task<bool> DeleteAsync(string cameraId, string instanceId, CancellationToken token = default);
}
